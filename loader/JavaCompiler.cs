using javax.tools;
using org.eclipse.jdt.@internal.compiler.tool;
using System;
using System.Linq;
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

class FakeFileManager : ForwardingJavaFileManager
{
	public readonly Dictionary<string, FakeOutputFile> ClassFiles = new();

	public FakeFileManager(JavaFileManager fm) : base(fm) {}

	public override javax.tools.JavaFileObject getJavaFileForOutput(
		javax.tools.JavaFileManager.Location location,
		string className,
		javax.tools.JavaFileObject.Kind kind,
		javax.tools.FileObject sibling)
	{
		var file = new FakeOutputFile(className);
		ClassFiles[className] = file;
		return file;
	}
}

class FakeClassLoader : java.lang.ClassLoader
{
	private readonly Dictionary<string, byte[]> ClassFiles = new();

	public FakeClassLoader(Dictionary<string, byte[]> classFiles)
	{
		ClassFiles = classFiles;
	}

    protected override java.lang.Class findClass(string name)
    {
        if (ClassFiles.TryGetValue(name.Replace(".", "/"), out var bytes))
        {
            return defineClass(name, bytes, 0, bytes.Length);
        }

        throw new java.lang.ClassNotFoundException(name);
    }
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
        if (name == "main" &&
            descriptor == "([Ljava/lang/String;)V" &&
            (access & org.objectweb.asm.Opcodes.ACC_PUBLIC) != 0 &&
            (access & org.objectweb.asm.Opcodes.ACC_STATIC) != 0)
        {
            HasMain = true;
        }
        return null;
    }
}

internal static class JavaCompiler {
	internal struct Result {
		public java.lang.ClassLoader ClassLoader;
		public Diagnostic[] Diagnostics;
		public string CompilerStdout;
		public string MainClass;
	}

	public static Result CompileJavaSource(Dictionary<string, string> sources) {
		EclipseCompiler compiler = new();
		DiagnosticCollector collector = new();
		java.io.StringWriter writer = new();

		var baseFm = compiler.getStandardFileManager(collector, null, null);
		FakeFileManager fm = new(baseFm);
		baseFm.setLocation(
			StandardLocation.PLATFORM_CLASS_PATH,
			java.util.Arrays.asList(new java.io.File("/libsdl/rt.jar")));
		baseFm.setLocation(
			StandardLocation.CLASS_PATH,
			java.util.Collections.emptyList());

		var sourceFiles = sources.Select(x => new FakeSourceFile(x.Key, x.Value)).ToArray();
		
		var task = compiler.getTask(writer, fm, collector, java.util.Arrays.asList("-source", "8", "-target", "8"), null, java.util.Arrays.asList(sourceFiles));
		var ok = task.call();

		var diags = collector.getDiagnostics().toArray().Cast<Diagnostic>().ToArray();

		if (ok.booleanValue()) {
			var classes = fm.ClassFiles.ToDictionary(x => x.Key, x => x.Value.Bytes.toByteArray());

			var main = classes.First(x => {
				var reader = new org.objectweb.asm.ClassReader(x.Value);
				MainFinderVisitor visitor = new();
				reader.accept(visitor, org.objectweb.asm.ClassReader.SKIP_CODE);
				return visitor.HasMain;
			}).Key;

			return new Result {
				Diagnostics = diags,
				CompilerStdout = writer.toString(),

				ClassLoader = new FakeClassLoader(classes),
				MainClass = main.Replace("/", "."),
			};
		} else {
			return new Result {
				Diagnostics = diags,
				CompilerStdout = writer.toString(),
			};
		}
	}
}
