using System;
using System.Collections.Generic;
using java.lang;

internal static class ExceptionLogging
{
    internal static void WriteException(System.Exception ex, string header)
    {
        if (!string.IsNullOrEmpty(header))
        {
            Console.Error.WriteLine(header);
        }

        Console.Error.WriteLine(ex);

        var javaThrowable = FindJavaThrowable(ex);
        if (javaThrowable != null)
        {
            javaThrowable.printStackTrace();
            PrintJavaThrowableChain(javaThrowable);
        }
    }

    // like printStackTrace, but through Console, which always reaches the page. takes .NET exceptions too: they are
    // mapped to their java equivalents (NullReferenceException -> NullPointerException, ...). userFramesOnly drops
    // the frames of the loader and of reflection, which are below every frame of a java program.
    internal static void PrintJavaThrowableChain(System.Exception e, bool userFramesOnly = false)
    {
        var seen = new HashSet<System.Exception>(ReferenceEqualityComparer.Instance);
        var current = ikvm.runtime.Util.mapException(e);
        var prefix = "";
        while (current != null && seen.Add(current))
        {
            var msg = ikvm.extensions.ExtensionMethods.getMessage(current);
            var className = ikvm.extensions.ExtensionMethods.getClass(current).getName();
            Console.Error.WriteLine($"{prefix}{className}{(msg != null ? ": " + msg : "")}");

            foreach (var frame in ikvm.extensions.ExtensionMethods.getStackTrace(current))
            {
                if (userFramesOnly && IsLauncherFrame(frame))
                    continue;
                Console.Error.WriteLine($"\tat {frame}");
            }

            current = ikvm.extensions.ExtensionMethods.getCause(current);
            prefix = "Caused by: ";
        }
    }

    private static bool IsLauncherFrame(StackTraceElement frame)
    {
        var className = frame.getClassName();
        return className.StartsWith("cli.", StringComparison.Ordinal) ||
            className.StartsWith("System.", StringComparison.Ordinal) ||
            className.StartsWith("IKVM.", StringComparison.Ordinal) ||
            className.StartsWith("java.lang.reflect.", StringComparison.Ordinal) ||
            className.StartsWith("jdk.internal.reflect.", StringComparison.Ordinal) ||
            // the program's thread starting
            (className == "java.lang.Thread" && frame.getMethodName() == "run");
    }

    private static Throwable FindJavaThrowable(System.Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is Throwable throwable)
            {
                return throwable;
            }
        }

        return null;
    }
}
