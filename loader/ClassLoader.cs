global using IkvmClassLoaderDll = (string[] Prefixes, string Name);
global using IkvmClassLoaderTransformer = System.Func<(string[] Prefixes, System.Type Visitor)>;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using IKVM.Runtime;

internal class SafeClassWriter(org.objectweb.asm.ClassReader reader, int flags) : org.objectweb.asm.ClassWriter(reader, flags)
{
	protected override string getCommonSuperClass(string type1, string type2)
	{
		try { return base.getCommonSuperClass(type1, type2); }
		catch (Exception) { return "java/lang/Object"; }
	}
}

internal sealed class IkvmClassLoader : java.net.URLClassLoader, ikvm.lang.LinkTrustPolicy
{
	// logs from any thread straight to the ui console (a JSImport only works on the thread that owns the JS context)
	[DllImport("Emscripten")]
	private static extern void classloader_debug([MarshalAs(UnmanagedType.LPUTF8Str)] string log);

	private readonly java.lang.ClassLoader systemLoader;
	private readonly List<(string Name, string[] Prefixes, java.lang.ClassLoader Loader)> dllLoaders;
	private readonly List<(string[] Prefixes, System.Type Visitor)> classTransformers;

	public IkvmClassLoader(string[] jars, IkvmClassLoaderDll[] dlls, IkvmClassLoaderTransformer[] transformers)
		: base((from jar in jars select new java.net.URL("file", "", jar)).ToArray(), null)
	{
		var selfLoader = CreateAssemblyClassLoader(typeof(IkvmClassLoader).Assembly);

		dllLoaders =
		[
			("IkvmWasm.self", new[] { "cli.Ikvm" }, selfLoader),
			.. from dll in dlls
			   select (dll.Name, dll.Prefixes, CreateAssemblyClassLoader(Assembly.Load(dll.Name))),
		];
		systemLoader = java.lang.ClassLoader.getSystemClassLoader();

		classTransformers = new();
		foreach (var transformerInit in transformers)
		{
			var transformer = transformerInit();
			Type baseType = transformer.Visitor;
			while (baseType != typeof(System.Object))
			{
				baseType = baseType.BaseType;
				if (baseType == typeof(org.objectweb.asm.ClassVisitor))
					goto End;
			}
			throw new InvalidOperationException("transformer must inherit from ClassVisitor");
		End:
			classTransformers.Add(transformer);
		}
	}

	private static java.lang.ClassLoader CreateAssemblyClassLoader(Assembly assembly)
	{
		var context = typeof(JVM).GetProperty("Context", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
		if (context is null)
		{
			throw new InvalidOperationException("IKVM runtime context is unavailable.");
		}

		var classLoaderFactory = context.GetType().GetProperty("AssemblyClassLoaderFactory", BindingFlags.Instance | BindingFlags.Public)?.GetValue(context);
		if (classLoaderFactory is null)
		{
			throw new InvalidOperationException("IKVM assembly class loader factory is unavailable.");
		}

		var loader = classLoaderFactory.GetType().GetMethod("FromAssembly", BindingFlags.Instance | BindingFlags.Public)?.Invoke(classLoaderFactory, [assembly]);
		if (loader is null)
		{
			throw new InvalidOperationException($"Failed creating IKVM class loader for assembly '{assembly.FullName}'.");
		}

		var javaClassLoader = loader.GetType().GetMethod("GetJavaClassLoader", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(loader, []);
		if (javaClassLoader is not java.lang.ClassLoader typedLoader)
		{
			throw new InvalidOperationException($"Failed obtaining Java class loader for assembly '{assembly.FullName}'.");
		}

		return typedLoader;
	}

	protected override java.lang.Class findClass(string name)
	{
		if (classTransformers.Count == 0)
		{
			return base.findClass(name);
		}

		var resourcePath = name.Replace('.', '/') + ".class";
		var url = findResource(resourcePath);
		if (url is null)
		{
			throw new java.lang.ClassNotFoundException(name);
		}

		byte[] bytes;
		var stream = url.openStream();
		try
		{
			var output = new java.io.ByteArrayOutputStream();
			var buffer = new byte[8192];
			int n;
			while ((n = stream.read(buffer)) > 0)
			{
				output.write(buffer, 0, n);
			}
			bytes = output.toByteArray();
		}
		finally
		{
			stream.close();
		}

		foreach (var transformer in classTransformers)
		{
			if (!transformer.Prefixes.Any(x => name.StartsWith(x, StringComparison.Ordinal)))
				continue;

			classloader_debug($"[IkvmClassLoader] applying transformer {transformer.Visitor} to '{name}'");

			try
			{
				org.objectweb.asm.ClassReader reader = new(bytes);
				org.objectweb.asm.ClassWriter writer = new SafeClassWriter(reader, org.objectweb.asm.ClassWriter.COMPUTE_FRAMES | org.objectweb.asm.ClassWriter.COMPUTE_MAXS);
				var visitor = (org.objectweb.asm.ClassVisitor)Activator.CreateInstance(transformer.Visitor, [name, writer]);
				reader.accept(visitor, 0);

				bytes = writer.toByteArray();
			}
			catch (Exception)
			{
				Console.Error.WriteLine($"[IkvmClassLoader] transformer {transformer.Visitor} failed on '{name}'");
				throw;
			}
		}

		return defineClass(name, bytes, 0, bytes.Length);
	}

	// under IKVM_LAZY_CLASS_LOADING, names this returns true for get bound eagerly at link time (full speed)
	// instead of through a lazily resolved indirection. only true for names served by the class library or
	// a statically compiled bundle, which load without side effects.
	public bool isEagerLinkSafe(string name)
	{
		if (name.StartsWith("java.", StringComparison.Ordinal) || name.StartsWith("javax.", StringComparison.Ordinal) || name.StartsWith("jdk.", StringComparison.Ordinal) || name.StartsWith("sun.", StringComparison.Ordinal))
			return true;

		foreach (var (_, prefixes, _) in dllLoaders)
			if (prefixes.Any(x => name.StartsWith(x, StringComparison.Ordinal)))
				return true;

		return false;
	}

	protected override java.lang.Class loadClass(string name, bool resolve)
	{
		lock (getClassLoadingLock(name))
		{
			string loadedFrom = "[already loaded]";
			java.lang.Class cls = findLoadedClass(name);
			bool fastpath = false;

			if (name.StartsWith("java.", StringComparison.Ordinal) || name.StartsWith("javax.", StringComparison.Ordinal) || name.StartsWith("sun.", StringComparison.Ordinal))
			{
				if (cls is null)
				{
					cls = systemLoader.loadClass(name);
				}

				if (resolve)
				{
					resolveClass(cls);
				}

				return cls;
			}

			if (!fastpath && cls is null)
			{
				// bundles can split a package (ecj and jdt core both have org.eclipse.jdt.internal.compiler), so a
				// class missing from the first bundle with its prefix may be in a later one
				java.lang.ClassNotFoundException miss = null;
				foreach (var (assemblyName, prefixes, loader) in dllLoaders)
				{
					if (!prefixes.Any(x => name.StartsWith(x, StringComparison.Ordinal)))
						continue;

					try
					{
						cls = loader.loadClass(name);
					}
					catch (java.lang.ClassNotFoundException e)
					{
						miss = e;
						continue;
					}
					loadedFrom = $"assembly {assemblyName}";
					fastpath = true;
					break;
				}
				if (cls is null && miss is not null)
					throw miss;
			}

			if (!fastpath && cls is null)
			{
				try
				{
					cls = base.loadClass(name, resolve);
					loadedFrom = "URLClassLoader";
					resolve = false;
				}
				catch (java.lang.ClassNotFoundException cnf)
				{
					Console.WriteLine($"[IkvmClassLoader] '{name}' miss in URLClassLoader: {cnf.getClass().getName()}: {cnf.getMessage()}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[IkvmClassLoader] '{name}' EXCEPTION in URLClassLoader: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				}
			}

			if (cls is null)
			{
				cls = systemLoader.loadClass(name);
				loadedFrom = "system";
			}

			if (resolve)
			{
				resolveClass(cls);
			}

			classloader_debug($"[IkvmClassLoader] '{name}' loaded from {loadedFrom}");
			return cls;
		}
	}
}
