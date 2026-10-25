using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Runtime.InteropServices.JavaScript;

static partial class IkvmWasm
{
	internal static void Main()
	{
		Console.WriteLine(":3");
	}

	public static string[][] ConvertJSObjectToStringArray(JSObject jsObject)
	{
		var outerLength = jsObject.GetPropertyAsInt32("length");
		var result = new string[outerLength][];

		for (int i = 0; i < outerLength; i++)
		{
			using var innerArray = jsObject.GetPropertyAsJSObject(i.ToString());
			var innerLength = innerArray!.GetPropertyAsInt32("length");
			result[i] = new string[innerLength];

			for (int j = 0; j < innerLength; j++)
			{
				result[i][j] = innerArray.GetPropertyAsString(j.ToString()) ?? string.Empty;
			}
		}

		return result;
	}

	[JSExport]
	internal static Task PreInit(string fetchbase, JSObject props)
	{
		try
		{
			Emscripten.InstallThreadLogForwarder();

			Emscripten.MountFetch(0, fetchbase + "/image", "/ikvm");
			// JDK 25's home: its configuration in conf/, data in lib/, and the libraries the JDK looks up by file before
			// it loads them (they are linked into the wasm; the image has placeholders)
			Emscripten.MountFetchDir(0, "/ikvm/bin");
			foreach (var lib in new[] { "zip", "nio", "net", "management", "management_ext", "prefs", "awt", "awt_headless", "fontmanager", "mlib_image", "lcms", "javajpeg" })
				Emscripten.MountFetchFile(0, $"/ikvm/bin/lib{lib}.so");
			Emscripten.MountFetchDir(0, "/ikvm/conf");
			Emscripten.MountFetchFile(0, "/ikvm/conf/logging.properties");
			Emscripten.MountFetchFile(0, "/ikvm/conf/net.properties");
			Emscripten.MountFetchDir(0, "/ikvm/conf/security");
			Emscripten.MountFetchFile(0, "/ikvm/conf/security/java.security");
			Emscripten.MountFetchDir(0, "/ikvm/conf/security/policy");
			Emscripten.MountFetchDir(0, "/ikvm/conf/security/policy/unlimited");
			Emscripten.MountFetchFile(0, "/ikvm/conf/security/policy/unlimited/default_local.policy");
			Emscripten.MountFetchFile(0, "/ikvm/conf/security/policy/unlimited/default_US_export.policy");
			Emscripten.MountFetchDir(0, "/ikvm/lib");
			Emscripten.MountFetchFile(0, "/ikvm/lib/tzdb.dat");
			Emscripten.MountFetchDir(0, "/ikvm/lib/security");
			Emscripten.MountFetchFile(0, "/ikvm/lib/security/cacerts");
			Emscripten.MountFetchFile(0, "/ikvm/lib/security/blocked.certs");
			Emscripten.MountFetchFile(0, "/ikvm/lib/security/public_suffix_list.dat");
			// ecj reads the JDK's version from here before it opens jrt:/
			Emscripten.MountFetchFile(0, "/ikvm/release");

			File.WriteAllText("/ikvm.properties", "ikvm.home=/ikvm");

			InitializeJava(props);

			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "Error in PreInit()!");
			return Task.FromException(e);
		}
	}

	// Keep Java type initialization out of PreInit's compilation, until the filesystem and
	// startup properties above are ready.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void InitializeJava(JSObject props)
	{
		var bundleDlls = IkvmcManifest.LoadEmbedded().AllDlls();
		java.lang.Thread.currentThread().setContextClassLoader(new IkvmClassLoader([], bundleDlls, []));

		java.lang.System.setProperty("java.awt.headless", "true");
		// ecj otherwise hands every compile's code generation to a thread of its own
		java.lang.System.setProperty("jdt.compiler.useSingleThread", "true");

		foreach (var prop in ConvertJSObjectToStringArray(props))
		{
			Console.WriteLine($"-D{prop[0]}={prop[1]}");
			java.lang.System.setProperty(prop[0], prop[1]);
		}
	}

	[JSExport]
	internal static Task Warmup()
	{
		try
		{
			JavaCompiler.Warmup();
			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "[IKVM] Warmup failed");
			return Task.FromException(e);
		}
	}

	[JSExport]
	internal static Task WarmupLanguage()
	{
		try
		{
			JavaLanguage.Warmup();
			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "[jdt] Warmup failed");
			return Task.FromException(e);
		}
	}

	// the editor's language features: json, or "null" if there's nothing (or they failed, which is logged)
	private static Task<string> Language(string name, Func<string> run)
	{
		try
		{
			return Task.FromResult(run());
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, $"[jdt] {name} failed");
			return Task.FromResult("null");
		}
	}

	private static Dictionary<string, string> Sources(JSObject sourcesObj) =>
		ConvertJSObjectToStringArray(sourcesObj).ToDictionary(x => x[0], x => x[1]);

	[JSExport]
	internal static Task<string> Diagnose(JSObject sourcesObj, string file) =>
		Language("Diagnose", () => JavaLanguage.Diagnose(Sources(sourcesObj), file));

	[JSExport]
	internal static Task<string> Hover(JSObject sourcesObj, string file, int offset) =>
		Language("Hover", () => JavaLanguage.Hover(Sources(sourcesObj), file, offset));

	[JSExport]
	internal static Task<string> Complete(JSObject sourcesObj, string file, int offset) =>
		Language("Complete", () => JavaLanguage.Complete(Sources(sourcesObj), file, offset));

	// start < 0 formats all of it
	[JSExport]
	internal static Task<string> Format(string source, int start, int end, int tabSize, bool insertSpaces) =>
		Language("Format", () => JavaLanguage.Format(source, start, end, tabSize, insertSpaces));

	// the program gets a java thread of its own, like the java launcher's main thread. every call from js runs on this
	// one (dotnet-worker-001), which has to stay free while a program runs: for the editor's requests, and for
	// anything else js asks for in the meantime.
	[JSExport]
	internal static Task RunJava(JSObject sourcesObj)
	{
		try
		{
			var sources = Sources(sourcesObj);
			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var thread = new java.lang.Thread(null, new JavaRunnable(() =>
			{
				Exception failure;
				try
				{
					failure = RunProgram(sources);
				}
				catch (Exception e)
				{
					ExceptionLogging.WriteException(e, "[IKVM] RunJava failed");
					failure = e;
				}

				try
				{
					FlushProgramOutput();
				}
				finally
				{
					// the page gets the program's output from this worker, and the result after it from
					// dotnet-worker-001: both are messages to the page, which it takes in the order they were sent
					if (failure == null)
						done.SetResult();
					else
						done.SetException(failure);
				}
			}), "main", 0);
			thread.start();
			return done.Task;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "[IKVM] RunJava failed");
			return Task.FromException(e);
		}
	}

	// compiles and runs a program, and answers why it failed (it reported that itself), or null
	private static Exception RunProgram(Dictionary<string, string> sources)
	{
		var result = JavaCompiler.CompileJavaSource(sources);

		// ecj reports problems with their source lines; the diagnostics are the same problems, without them
		if (!string.IsNullOrWhiteSpace(result.CompilerStdout))
		{
			Console.Error.WriteLine(result.CompilerStdout.TrimEnd());
		}
		else
		{
			foreach (var diag in result.Diagnostics)
				Console.Error.WriteLine($"[{diag.getKind()}] line {diag.getLineNumber()}: {diag.getMessage(null)}");
		}

		if (result.ClassLoader == null)
			return new InvalidOperationException("Java compile failed");
		if (result.MainClass == null)
		{
			Console.Error.WriteLine("error: no main method found");
			return new InvalidOperationException("No main method found");
		}

		var mainClass = java.lang.Class.forName(result.MainClass, true, result.ClassLoader);
		try
		{
			JavaCompiler.LaunchMain(mainClass, []);
		}
		catch (JavaProgramException e)
		{
			// report it like the java launcher would, after what the program printed
			FlushProgramOutput();
			Console.Error.Write("Exception in thread \"main\" ");
			ExceptionLogging.PrintJavaThrowableChain(e.InnerException, userFramesOnly: true);
			return e.InnerException;
		}

		return null;
	}

	// as the jvm does when it exits, down to a last line without a newline
	private static void FlushProgramOutput()
	{
		java.lang.System.@out.flush();
		java.lang.System.err.flush();
		Emscripten.FlushStdStreams();
	}
}

internal sealed class JavaRunnable(Action body) : java.lang.Runnable
{
	public void run() => body();
}
