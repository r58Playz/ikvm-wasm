using System;
using System.Linq;
using System.IO;
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
			Emscripten.MountOpfs();

			Emscripten.MountFetch(0, fetchbase + "/image", "/ikvm");
			Emscripten.MountFetchDir(0, "/ikvm/bin");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libzip.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libnio.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libnet.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libmanagement.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libawt.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libfontmanager.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libmlib_image.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/liblcms.so");
			Emscripten.MountFetchFile(0, "/ikvm/bin/libjpeg.so");
			Emscripten.MountFetchDir(0, "/ikvm/lib");
			Emscripten.MountFetchFile(0, "/ikvm/lib/currency.data");
			Emscripten.MountFetchFile(0, "/ikvm/lib/tzdb.dat");
			Emscripten.MountFetchFile(0, "/ikvm/lib/content-types.properties");
			Emscripten.MountFetchFile(0, "/ikvm/lib/logging.properties");

			Emscripten.MountFetch(1, fetchbase + "/assets", "/assets");
			Emscripten.MountFetchFile(1, "/assets/rt.jar");

			File.WriteAllText("/ikvm.properties", "ikvm.home=/ikvm");

			// -- ikvm will init after this --
			var bundleDlls = IkvmcManifest.LoadEmbedded().AllDlls();
			java.lang.Thread.currentThread().setContextClassLoader(new IkvmClassLoader([], bundleDlls, []));

			java.lang.System.setProperty("java.awt.headless", "true");

			foreach (var prop in ConvertJSObjectToStringArray(props))
			{
				Console.WriteLine($"-D{prop[0]}={prop[1]}");
				java.lang.System.setProperty(prop[0], prop[1]);
			}

			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "Error in PreInit()!");
			return Task.FromException(e);
		}
	}

	[JSExport]
	internal static Task RunJava(JSObject sourcesObj)
	{
		try
		{
			var sources = ConvertJSObjectToStringArray(sourcesObj).ToDictionary(x => x[0], x => x[1]);
			var result = JavaCompiler.CompileJavaSource(sources);

			Console.WriteLine(result.CompilerStdout);
			foreach (var diag in result.Diagnostics) {
				Console.WriteLine($"[{diag.getKind()}] line {diag.getLineNumber()}: {diag.getMessage(null)}");
			}

			if (result.ClassLoader != null) {
				var mainClass = java.lang.Class.forName(result.MainClass, true, result.ClassLoader);
				var stringArrayClass = java.lang.Class.forName("[Ljava.lang.String;");
				var mainMethod = mainClass.getMethod("main", new[] { stringArrayClass });
				mainMethod.invoke(null, new object[] { Array.Empty<string>() });
			} else {
				throw new InvalidOperationException("Java compile failed");
			}

			return Task.CompletedTask;
		}
		catch (Exception e)
		{
			ExceptionLogging.WriteException(e, "[IKVM] RunJava failed");
			return Task.FromException(e);
		}
	}
}
