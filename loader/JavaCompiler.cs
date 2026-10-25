using javax.tools;
using org.eclipse.jdt.@internal.compiler.tool;
using org.eclipse.jdt.@internal.compiler.util;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

class FakeSourceFile : SimpleJavaFileObject
{
	private readonly string Source;

	public FakeSourceFile(string className, string src) : base(java.net.URI.create($"string:///{className.Replace(".", "/")}.java"), JavaFileObject.Kind.SOURCE)
	{
		Source = src;
	}

	public override java.lang.CharSequence getCharContent(bool ignoreErrors) => Source;
}

class FakeOutputFile : SimpleJavaFileObject
{
	public readonly java.io.ByteArrayOutputStream Bytes = new();

	public FakeOutputFile(string className) : base(java.net.URI.create($"bytes:///{className.Replace(".", "/")}.class"), JavaFileObject.Kind.CLASS)
	{ }

	public override java.io.OutputStream openOutputStream() => Bytes;
}

// an EclipseFileManager whose constructor skips initialize(), which walks every class file of jrt:/ each time. with a
// file manager that isn't a standard one, ecj reads the system modules from jrt:/ itself.
sealed class NoSystemFileManager : EclipseFileManager
{
	public NoSystemFileManager() : base(null, null) { }

	protected override void initialize(java.io.File javahome) { }
}

class FakeFileManager : ForwardingJavaFileManager
{
	public readonly Dictionary<string, FakeOutputFile> ClassFiles = new();

	public FakeFileManager() : base(new NoSystemFileManager()) { }

	// ecj only compiles units it finds on SOURCE_PATH or on disk
	public override bool hasLocation(javax.tools.JavaFileManager.Location location) => ReferenceEquals(location, StandardLocation.SOURCE_PATH);

	public override bool contains(javax.tools.JavaFileManager.Location location, javax.tools.FileObject fo) =>
		ReferenceEquals(location, StandardLocation.SOURCE_PATH) && fo is FakeSourceFile;

	public override java.lang.Iterable list(javax.tools.JavaFileManager.Location location, string packageName, java.util.Set kinds, bool recurse) =>
		java.util.Collections.emptyList();

	public override javax.tools.JavaFileObject getJavaFileForInput(javax.tools.JavaFileManager.Location location, string className, javax.tools.JavaFileObject.Kind kind) => null;

	public override javax.tools.JavaFileObject getJavaFileForOutput(
		javax.tools.JavaFileManager.Location location,
		string className,
		javax.tools.JavaFileObject.Kind kind,
		javax.tools.FileObject sibling)
	{
		var file = new FakeOutputFile(className);
		ClassFiles[className.Replace('/', '.')] = file;
		return file;
	}
}

class FakeClassLoader : java.lang.ClassLoader, ikvm.lang.LinkTrustPolicy
{
	private readonly Dictionary<string, byte[]> ClassFiles;

	public FakeClassLoader(Dictionary<string, byte[]> classFiles)
	{
		ClassFiles = classFiles;
	}

	protected override java.lang.Class findClass(string name)
	{
		if (ClassFiles.TryGetValue(name, out var bytes))
		{
			return defineClass(name, bytes, 0, bytes.Length);
		}

		throw new java.lang.ClassNotFoundException(name);
	}

	// the program's classes are all in memory and load without side effects, so IKVM may bind them eagerly
	public bool isEagerLinkSafe(string name) => true;
}

class MainFinderVisitor : org.objectweb.asm.ClassVisitor
{
	public bool HasMain { get; private set; }

	public MainFinderVisitor()
		: base(org.objectweb.asm.Opcodes.ASM9) { }

	public override org.objectweb.asm.MethodVisitor visitMethod(
		int access, string name, string descriptor,
		string signature, string[] exceptions)
	{
		// JEP 512: main need not be public or static, and may take no arguments
		if (name == "main" &&
			(descriptor == "([Ljava/lang/String;)V" || descriptor == "()V") &&
			(access & org.objectweb.asm.Opcodes.ACC_PRIVATE) == 0)
		{
			HasMain = true;
		}
		return null;
	}
}

// what the program itself threw, as opposed to a failure to compile or launch it
internal sealed class JavaProgramException(Exception thrown) : Exception("The Java program threw an exception", thrown);

internal static class JavaCompiler {
	internal struct Result {
		public java.lang.ClassLoader ClassLoader;
		public Diagnostic[] Diagnostics;
		public string CompilerStdout;
		public string MainClass;
	}

	// ecj opens java.home's modules with FileSystems.newFileSystem(jrt:/, {java.home}), which, as on a stock JDK, loads
	// the jrt provider from <java.home>/lib/jrt-fs.jar. IKVM has no such jar, but its own jrt:/ is java.home's, and
	// ecj looks in its cache of file systems by java.home first.
	internal static readonly Lazy<bool> JrtSeeded = new(() =>
	{
		var field = ikvm.runtime.Util.getClassFromTypeHandle(typeof(JRTUtil).TypeHandle).getDeclaredField("JRT_FILE_SYSTEMS");
		field.setAccessible(true);
		var home = new java.io.File(java.lang.System.getProperty("java.home"));
		((java.util.Map)field.get(null)).putIfAbsent(home.toPath().toAbsolutePath().normalize(), java.nio.file.FileSystems.getFileSystem(java.net.URI.create("jrt:/")));
		// open ecj's view of the system modules now (it walks jrt:/'s packages), which reports the real cause if it
		// fails: in a compile it is only "Failed to init packages"
		JRTUtil.getJrtSystem(home, null);
		return true;
	});

	internal static readonly string Release = Math.Min(java.lang.Runtime.version().feature(), 26).ToString();

	public static Result CompileJavaSource(Dictionary<string, string> sources) {
		_ = JrtSeeded.Value;

		EclipseCompiler compiler = new();
		DiagnosticCollector collector = new();
		java.io.StringWriter writer = new();
		FakeFileManager fm = new();

		var sourceFiles = sources.Select(x => new FakeSourceFile(x.Key, x.Value)).ToArray();

		var task = compiler.getTask(writer, fm, collector, java.util.Arrays.asList("--release", Release, "-proc:none"), null, java.util.Arrays.asList(sourceFiles));
		var ok = task.call();

		var diags = collector.getDiagnostics().toArray().Cast<Diagnostic>().ToArray();

		if (!ok.booleanValue()) {
			return new Result {
				Diagnostics = diags,
				CompilerStdout = writer.toString(),
			};
		}

		var classes = fm.ClassFiles.ToDictionary(x => x.Key, x => x.Value.Bytes.toByteArray());

		var mains = classes.Where(x => {
			var reader = new org.objectweb.asm.ClassReader(x.Value);
			MainFinderVisitor visitor = new();
			reader.accept(visitor, org.objectweb.asm.ClassReader.SKIP_CODE);
			return visitor.HasMain;
		}).Select(x => x.Key).ToArray();

		// like `java Main.java`, prefer the class the first file is named after
		var first = sources.Keys.First();
		var main = mains.FirstOrDefault(x => x == first || x.EndsWith("." + first, StringComparison.Ordinal)) ?? mains.FirstOrDefault();

		return new Result {
			Diagnostics = diags,
			CompilerStdout = writer.toString(),

			ClassLoader = new FakeClassLoader(classes),
			MainClass = main,
		};
	}

	// JEP 512's launch protocol: main(String[]) before main(), static or not, as long as it isn't private.
	// main is found through java reflection but invoked through .NET's: java's setAccessible won't open a member of a
	// class that isn't public (the class of a compact source file) to a caller without a java frame, which this is.
	public static void LaunchMain(java.lang.Class cls, string[] args)
	{
		var stringArrayClass = java.lang.Class.forName("[Ljava.lang.String;");
		foreach (var parameterTypes in new[] { new[] { stringArrayClass }, new java.lang.Class[0] })
		{
			var method = FindMain(cls, parameterTypes);
			if (method == null)
				continue;

			var withArgs = parameterTypes.Length == 1;
			var declaringType = ikvm.runtime.Util.getInstanceTypeFromClass(method.getDeclaringClass());
			var netMethod = declaringType.GetMethod("main", BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
				null, withArgs ? [typeof(string[])] : Type.EmptyTypes, null)
				?? throw new MissingMethodException(declaringType.FullName, "main");

			try
			{
				object instance = null;
				if (!netMethod.IsStatic)
					instance = Activator.CreateInstance(ikvm.runtime.Util.getInstanceTypeFromClass(cls), nonPublic: true);
				netMethod.Invoke(instance, BindingFlags.DoNotWrapExceptions, null, withArgs ? [args] : null, null);
			}
			catch (Exception e)
			{
				// as a java catch would: .NET exceptions (NullReferenceException, ...) become their java equivalents,
				// with the stack trace of where they were thrown
				throw new JavaProgramException(IKVM.Runtime.ByteCodeHelper.MapException<Exception>(e, IKVM.Runtime.ByteCodeHelper.MapFlags.None));
			}
			return;
		}

		throw new InvalidOperationException($"No main method in {cls.getName()}");
	}

	private static java.lang.reflect.Method FindMain(java.lang.Class cls, java.lang.Class[] parameterTypes)
	{
		for (var c = cls; c != null; c = c.getSuperclass())
		{
			try
			{
				var method = c.getDeclaredMethod("main", parameterTypes);
				if (!java.lang.reflect.Modifier.isPrivate(method.getModifiers()) && method.getReturnType() == java.lang.Void.TYPE)
					return method;
			}
			catch (java.lang.NoSuchMethodException) { }
		}

		return null;
	}

	// exercises the language features (and their bootstrap methods) a typical program uses, so the first real run
	// doesn't pay for loading and jitting the compiler
	private const string WarmupSource = """"
		import java.util.List;

		public class Warmup {
			sealed interface Shape permits Circle, Square {}
			record Circle(double r) implements Shape {}
			record Square(double s) implements Shape {}

			static double area(Shape shape) {
				return switch (shape) {
					case Circle c -> Math.PI * c.r() * c.r();
					case Square s -> s.s() * s.s();
				};
			}

			public static void main(String[] args) {
				var shapes = List.of(new Circle(1), new Square(2));
				var total = shapes.stream().mapToDouble(Warmup::area).sum();
				var text = """
					%s: %.2f
					""".formatted(shapes, total);
				if (text.isEmpty() || ("" + total + shapes.get(0)).isEmpty()) throw new IllegalStateException();
			}
		}
		"""";

	public static void Warmup()
	{
		var sw = Stopwatch.StartNew();
		_ = JrtSeeded.Value;
		var seeded = sw.ElapsedMilliseconds;

		var result = CompileJavaSource(new() { ["Warmup"] = WarmupSource });
		var compiled = sw.ElapsedMilliseconds;
		if (result.ClassLoader == null)
		{
			foreach (var diag in result.Diagnostics)
				Console.Error.WriteLine($"[warmup] [{diag.getKind()}] line {diag.getLineNumber()}: {diag.getMessage(null)}");
			throw new InvalidOperationException("Warmup compile failed");
		}

		LaunchMain(java.lang.Class.forName(result.MainClass, true, result.ClassLoader), []);
		Console.WriteLine($"[warmup] jrt:/ {seeded}ms, compile {compiled - seeded}ms, run {sw.ElapsedMilliseconds - compiled}ms");
	}
}
