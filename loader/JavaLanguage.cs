using org.eclipse.jdt.core;
using org.eclipse.jdt.core.dom;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AccessRestriction = org.eclipse.jdt.@internal.compiler.env.AccessRestriction;
using CodeFormatter = org.eclipse.jdt.core.formatter.CodeFormatter;
using DefaultCodeFormatterConstants = org.eclipse.jdt.core.formatter.DefaultCodeFormatterConstants;
using DeleteEdit = org.eclipse.text.edits.DeleteEdit;
using InsertEdit = org.eclipse.text.edits.InsertEdit;
using Region = org.eclipse.jface.text.Region;
using ReplaceEdit = org.eclipse.text.edits.ReplaceEdit;
using TextEdit = org.eclipse.text.edits.TextEdit;
using BatchFileSystem = org.eclipse.jdt.@internal.compiler.batch.FileSystem;
using CharOperation = org.eclipse.jdt.core.compiler.CharOperation;
using ClassFileConstants = org.eclipse.jdt.@internal.compiler.classfmt.ClassFileConstants;
using CompletionEngine = org.eclipse.jdt.@internal.codeassist.CompletionEngine;
using DomUnit = org.eclipse.jdt.core.dom.CompilationUnit;
using ICompilerUnit = org.eclipse.jdt.@internal.compiler.env.ICompilationUnit;
using IJavaSearchConstants = org.eclipse.jdt.core.search.IJavaSearchConstants;
using IModule = org.eclipse.jdt.@internal.compiler.env.IModule;
using IProblem = org.eclipse.jdt.core.compiler.IProblem;
using IProgressMonitor = org.eclipse.core.runtime.IProgressMonitor;
using ISearchRequestor = org.eclipse.jdt.@internal.codeassist.ISearchRequestor;
using IUpdatableModule = org.eclipse.jdt.@internal.compiler.env.IUpdatableModule;
using JavaProject = org.eclipse.jdt.@internal.core.JavaProject;
using ModuleBinding = org.eclipse.jdt.@internal.compiler.lookup.ModuleBinding;
using NameEnvironmentAnswer = org.eclipse.jdt.@internal.compiler.env.NameEnvironmentAnswer;
using SearchableEnvironment = org.eclipse.jdt.@internal.core.SearchableEnvironment;
using SearchPattern = org.eclipse.jdt.core.search.SearchPattern;
using SourceUnit = org.eclipse.jdt.@internal.compiler.batch.CompilationUnit;

// the class library's types, through ecj's view of jrt:/, and the source types of the editor's other files
internal sealed class PlaygroundNameEnvironment
{
	private sealed class JrtFileSystem(BatchFileSystem.Classpath[] paths) : BatchFileSystem(paths, null, false, null, null);

	public readonly record struct IndexedType(char[] Package, char[] Name, char[] Module);

	public readonly BatchFileSystem Jrt;
	private readonly BatchFileSystem.Classpath[] classpaths;

	// binary types never change, and the lookup asks for the same ones (and the same missing ones) on every request.
	// the answers themselves can't be shared: a lookup environment stores its module binding in the ones it gets.
	private readonly Dictionary<string, NameEnvironmentAnswer> binaryCache = new();
	private readonly Dictionary<string, int> modifierCache = new();
	private List<IndexedType> typeIndex;
	private List<char[]> packages;

	// main type name -> source, of every file but the one being completed
	public Dictionary<string, string> Sources = new();

	public PlaygroundNameEnvironment()
	{
		var list = new java.util.ArrayList();
		org.eclipse.jdt.@internal.compiler.util.Util.collectRunningVMBootclasspath(list);
		classpaths = (BatchFileSystem.Classpath[])list.toArray(new BatchFileSystem.Classpath[0]);
		Jrt = new JrtFileSystem(classpaths);
	}

	// ANY, ANY_NAMED and UNNAMED are all "", told apart by identity
	private static string ModuleKey(char[] moduleName) =>
		ReferenceEquals(moduleName, ModuleBinding.ANY) ? "*any" :
		ReferenceEquals(moduleName, ModuleBinding.ANY_NAMED) ? "*named" :
		ReferenceEquals(moduleName, ModuleBinding.UNNAMED) ? "*unnamed" :
		moduleName == null ? "*null" : new string(moduleName);

	public NameEnvironmentAnswer FindType(char[][] compoundName, char[] moduleName)
	{
		if (compoundName.Length == 1 && Sources.TryGetValue(new string(compoundName[0]), out var source))
			return new NameEnvironmentAnswer(new SourceUnit(source.ToCharArray(), new string(compoundName[0]) + ".java", "UTF-8"), (AccessRestriction)null);

		var key = new string(CharOperation.concatWith(compoundName, '.')) + "@" + ModuleKey(moduleName);
		if (!binaryCache.TryGetValue(key, out var answer))
			binaryCache[key] = answer = Jrt.findType(compoundName, moduleName);
		return answer == null || !answer.isBinaryType()
			? answer
			: new NameEnvironmentAnswer(answer.getBinaryType(), answer.getAccessRestriction(), answer.moduleName());
	}

	public List<IndexedType> Types
	{
		get
		{
			EnsureIndex();
			return typeIndex;
		}
	}

	public List<char[]> Packages
	{
		get
		{
			EnsureIndex();
			return packages;
		}
	}

	// the top-level types of every package the class library exports to everyone
	private void EnsureIndex()
	{
		if (typeIndex != null)
			return;

		var jrt = java.nio.file.FileSystems.getFileSystem(java.net.URI.create("jrt:/"));
		var types = new List<IndexedType>();
		var exported = new List<char[]>();
		var modules = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var classpath in classpaths)
			for (var names = classpath.getModuleNames(null).iterator(); names.hasNext();)
				modules.Add((string)names.next());

		foreach (var module in modules)
		{
			var descriptor = Jrt.getModule(module.ToCharArray());
			if (descriptor?.exports() is not { } exports)
				continue;

			foreach (var export in exports)
			{
				if (export.targets() is { Length: > 0 })
					continue;

				var package = new string(export.name());
				exported.Add(export.name());
				var stream = java.nio.file.Files.newDirectoryStream(jrt.getPath("/modules", [module, .. package.Split('.')]));
				try
				{
					for (var files = stream.iterator(); files.hasNext();)
					{
						var file = ((java.nio.file.Path)files.next()).getFileName().toString();
						if (file.EndsWith(".class", StringComparison.Ordinal) && !file.Contains('$') && file != "package-info.class")
							types.Add(new IndexedType(export.name(), file[..^".class".Length].ToCharArray(), module.ToCharArray()));
					}
				}
				finally
				{
					stream.close();
				}
			}
		}

		packages = exported;
		typeIndex = types;
	}

	// -1 if the type can't be read
	public int Modifiers(IndexedType type)
	{
		var key = new string(type.Package) + "." + new string(type.Name);
		if (!modifierCache.TryGetValue(key, out var modifiers))
		{
			var answer = FindType(CharOperation.arrayConcat(CharOperation.splitOn('.', type.Package), type.Name), type.Module);
			modifierCache[key] = modifiers = answer?.getBinaryType()?.getModifiers() ?? -1;
		}
		return modifiers;
	}
}

// code assist's view of the playground. a SearchableEnvironment searches a java project and its index, which need a
// workspace; this one answers from a PlaygroundNameEnvironment. it is never constructed, since its constructors need a
// project: every method that would touch the project's state is overridden instead.
internal sealed class PlaygroundSearchableEnvironment : SearchableEnvironment
{
	// a cap on the type names proposed, each of which has to be read for its modifiers
	private const int MaxTypes = 200;

	private PlaygroundNameEnvironment env;

	private PlaygroundSearchableEnvironment() : base((JavaProject)null, (WorkingCopyOwner)null) { }

	public static PlaygroundSearchableEnvironment Create(PlaygroundNameEnvironment env)
	{
		var self = (PlaygroundSearchableEnvironment)RuntimeHelpers.GetUninitializedObject(typeof(PlaygroundSearchableEnvironment));
		self.env = env;
		return self;
	}

	public override NameEnvironmentAnswer findType(char[][] compoundTypeName, char[] moduleName) =>
		compoundTypeName == null ? null : env.FindType(compoundTypeName, moduleName);

	public override NameEnvironmentAnswer findType(char[] name, char[][] packageName, char[] moduleName) =>
		name == null ? null : env.FindType(CharOperation.arrayConcat(packageName ?? CharOperation.NO_CHAR_CHAR, name), moduleName);

	public override NameEnvironmentAnswer findTypeInModules(char[][] compoundTypeName, ModuleBinding module)
	{
		var nameForLookup = module.nameForLookup();
		var answer = findType(compoundTypeName, nameForLookup);
		if (answer != null || ReferenceEquals(nameForLookup, ModuleBinding.ANY) || ReferenceEquals(nameForLookup, ModuleBinding.ANY_NAMED) || ReferenceEquals(nameForLookup, ModuleBinding.UNNAMED))
			return answer;
		foreach (var required in module.getAllRequiredModules())
		{
			answer = findType(compoundTypeName, required.nameForLookup());
			if (answer != null)
				return answer;
		}
		return null;
	}

	public override char[][] getModulesDeclaringPackage(char[][] packageName, char[] moduleName) => env.Jrt.getModulesDeclaringPackage(packageName, moduleName);
	public override bool hasCompilationUnit(char[][] pkgName, char[] moduleName, bool checkCUs) => env.Jrt.hasCompilationUnit(pkgName, moduleName, checkCUs);
	public override IModule getModule(char[] name) => env.Jrt.getModule(name);
	public override char[][] getAllAutomaticModules() => env.Jrt.getAllAutomaticModules();
	public override char[][] listPackages(char[] moduleName) => env.Jrt.listPackages(moduleName);
	public override bool isOnModulePath(ICompilerUnit unit) => false;
	public override void applyModuleUpdates(IUpdatableModule module, IUpdatableModule.UpdateKind kind) { }
	public override void cleanup() { }

	public override void findTypes(char[] prefix, bool findMembers, bool camelCaseMatch, int searchFor, ISearchRequestor storage) =>
		FindTypes(prefix, SearchPattern.R_PREFIX_MATCH | (camelCaseMatch ? SearchPattern.R_CAMELCASE_MATCH : 0), searchFor, storage);

	public override void findTypes(char[] prefix, bool findMembers, int matchRule, int searchFor, ISearchRequestor storage, IProgressMonitor monitor) =>
		FindTypes(prefix, matchRule, searchFor, storage);

	public override void findTypes(char[] prefix, bool findMembers, int matchRule, int searchFor, bool resolveDocumentName, ISearchRequestor storage, IProgressMonitor monitor) =>
		FindTypes(prefix, matchRule, searchFor, storage);

	public override void findExactTypes(char[] name, bool findMembers, int searchFor, ISearchRequestor storage) =>
		FindTypes(name, SearchPattern.R_EXACT_MATCH, searchFor, storage);

	// only asked for when the requestor allows proposals that need others (it doesn't)
	public override void findConstructorDeclarations(char[] prefix, int matchRule, bool resolveDocumentName, ISearchRequestor storage, IProgressMonitor monitor) { }

	public override void findModules(char[] prefix, ISearchRequestor requestor, IJavaProject javaProject) { }

	public override void findPackages(char[] prefix, ISearchRequestor requestor)
	{
		foreach (var package in env.Packages)
			if (CharOperation.prefixEquals(prefix, package, false))
				requestor.acceptPackage(package);
	}

	public override void findPackages(char[] prefix, ISearchRequestor requestor, IPackageFragmentRoot[] moduleContext, bool followRequires) =>
		findPackages(prefix, requestor);

	private void FindTypes(char[] prefix, int matchRule, int searchFor, ISearchRequestor storage)
	{
		var dot = CharOperation.lastIndexOf('.', prefix);
		var qualifier = dot < 0 ? null : CharOperation.subarray(prefix, 0, dot);
		var simple = dot < 0 ? prefix : CharOperation.subarray(prefix, dot + 1, -1);
		// every type in the class library is too many to read
		if (simple.Length == 0 && qualifier == null)
			return;

		var exact = matchRule == SearchPattern.R_EXACT_MATCH;
		var camelCase = (matchRule & SearchPattern.R_CAMELCASE_MATCH) != 0;
		bool Matches(char[] name) => exact
			? CharOperation.equals(simple, name)
			: CharOperation.prefixEquals(simple, name, false) || (camelCase && CharOperation.camelCaseMatch(simple, name));

		if (qualifier == null)
			foreach (var name in env.Sources.Keys)
				if (Matches(name.ToCharArray()))
					storage.acceptType(CharOperation.NO_CHAR, name.ToCharArray(), CharOperation.NO_CHAR_CHAR, ClassFileConstants.AccPublic, null);

		var found = 0;
		foreach (var type in env.Types)
		{
			if (qualifier != null && !CharOperation.equals(qualifier, type.Package))
				continue;
			if (!Matches(type.Name))
				continue;

			var modifiers = env.Modifiers(type);
			if (modifiers < 0 || !Accepts(searchFor, modifiers))
				continue;

			storage.acceptType(type.Package, type.Name, CharOperation.NO_CHAR_CHAR, modifiers, null);
			if (++found >= MaxTypes)
				break;
		}
	}

	private static bool Accepts(int searchFor, int modifiers)
	{
		var isAnnotation = (modifiers & ClassFileConstants.AccAnnotation) != 0;
		var isInterface = !isAnnotation && (modifiers & ClassFileConstants.AccInterface) != 0;
		var isEnum = (modifiers & ClassFileConstants.AccEnum) != 0;
		var isClass = !isAnnotation && !isInterface && !isEnum;

		if (searchFor == IJavaSearchConstants.CLASS) return isClass;
		if (searchFor == IJavaSearchConstants.INTERFACE) return isInterface;
		if (searchFor == IJavaSearchConstants.ENUM) return isEnum;
		if (searchFor == IJavaSearchConstants.ANNOTATION_TYPE) return isAnnotation;
		if (searchFor == IJavaSearchConstants.CLASS_AND_ENUM) return isClass || isEnum;
		if (searchFor == IJavaSearchConstants.CLASS_AND_INTERFACE) return isClass || isInterface;
		if (searchFor == IJavaSearchConstants.INTERFACE_AND_ANNOTATION) return isInterface || isAnnotation;
		return true;
	}
}

// the java project code assist asks for its name and options. IJavaProject is far too big to implement for that.
internal sealed class PlaygroundProject(java.util.Map options) : java.lang.reflect.InvocationHandler
{
	public static IJavaProject Create(java.util.Map options)
	{
		var type = ikvm.runtime.Util.getClassFromTypeHandle(typeof(IJavaProject).TypeHandle);
		return (IJavaProject)java.lang.reflect.Proxy.newProxyInstance(type.getClassLoader(), [type], new PlaygroundProject(options));
	}

	public object invoke(object proxy, java.lang.reflect.Method method, object[] args)
	{
		switch (method.getName())
		{
			case "getOption": return options.get(args[0]);
			case "getOptions": return new java.util.Hashtable(options);
			case "getElementName": return "playground";
			case "getAllPackageFragmentRoots": return new IPackageFragmentRoot[0];
			case "hashCode": return java.lang.Integer.valueOf(RuntimeHelpers.GetHashCode(proxy));
			case "equals": return java.lang.Boolean.valueOf(ReferenceEquals(proxy, args[0]));
			case "toString": return "PlaygroundProject";
		}

		var type = method.getReturnType();
		if (type == java.lang.Boolean.TYPE) return java.lang.Boolean.FALSE;
		if (type == java.lang.Integer.TYPE) return java.lang.Integer.valueOf(0);
		if (type == java.lang.Long.TYPE) return java.lang.Long.valueOf(0);
		return null;
	}
}

internal sealed class ProposalCollector : CompletionRequestor
{
	public readonly List<CompletionProposal> Proposals = new();

	public ProposalCollector() : base(false) { }

	public override void accept(CompletionProposal proposal) => Proposals.Add(proposal);
}

// the editor's language features, on jdt core without a workspace: problems and hovers come from ASTParser, completions
// from the code assist engine, formatting from the eclipse formatter. results are json for the frontend.
internal static class JavaLanguage
{
	private const string SourceRoot = "/playground";

	private static readonly object Lock = new();
	private static java.util.Map options;
	private static PlaygroundNameEnvironment environment;
	private static IJavaProject project;

	// the last parse, for hovers between edits
	private static string cachedKey;
	private static DomUnit cachedUnit;

	private static void EnsureInitialized()
	{
		if (options != null)
			return;

		_ = JavaCompiler.JrtSeeded.Value;
		var defaults = JavaCore.getOptions();
		JavaCore.setComplianceOptions(JavaCompiler.Release, defaults);
		defaults.put(JavaCore.CODEASSIST_VISIBILITY_CHECK, JavaCore.ENABLED);
		environment = new PlaygroundNameEnvironment();
		project = PlaygroundProject.Create(defaults);
		options = defaults;
	}

	// ASTParser finds the other files on a source path, which mustn't have the file being parsed
	private static string[] SourcePath(Dictionary<string, string> sources, string file)
	{
		if (sources.Count == 1)
			return null;

		var dir = $"{SourceRoot}/{Path.GetFileNameWithoutExtension(file)}";
		Directory.CreateDirectory(dir);
		foreach (var existing in Directory.GetFiles(dir))
			if (!sources.ContainsKey(Path.GetFileName(existing)) || Path.GetFileName(existing) == file)
				File.Delete(existing);
		foreach (var (name, source) in sources)
			if (name != file)
				File.WriteAllText(Path.Combine(dir, name), source);
		return [dir];
	}

	private static DomUnit Parse(Dictionary<string, string> sources, string file)
	{
		var key = file + "\0" + string.Join("\0", sources.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "\0" + x.Value));
		if (key == cachedKey)
			return cachedUnit;

		var parser = ASTParser.newParser(AST.getJLSLatest());
		parser.setKind(ASTParser.K_COMPILATION_UNIT);
		parser.setCompilerOptions(options);
		parser.setResolveBindings(true);
		parser.setBindingsRecovery(true);
		parser.setStatementsRecovery(true);
		var sourcePath = SourcePath(sources, file);
		parser.setEnvironment([], sourcePath, sourcePath == null ? null : ["UTF-8"], true);
		parser.setUnitName(file);
		parser.setSource(sources[file].ToCharArray());
		var unit = (DomUnit)parser.createAST(null);

		cachedKey = key;
		cachedUnit = unit;
		return unit;
	}

	public static string Diagnose(Dictionary<string, string> sources, string file)
	{
		lock (Lock)
		{
			EnsureInitialized();
			var sw = Stopwatch.StartNew();
			var unit = Parse(sources, file);
			var ms = sw.Elapsed.TotalMilliseconds;

			return Json(w =>
			{
				w.WriteNumber("ms", ms);
				w.WriteStartArray("problems");
				foreach (var problem in unit.getProblems())
				{
					w.WriteStartObject();
					w.WriteString("severity", problem.isError() ? "error" : problem.isWarning() ? "warning" : "info");
					w.WriteString("message", problem.getMessage());
					w.WriteNumber("start", problem.getSourceStart());
					w.WriteNumber("end", problem.getSourceEnd() + 1);
					w.WriteNumber("id", problem.getID() & IProblem.IgnoreCategoriesMask);
					w.WriteEndObject();
				}
				w.WriteEndArray();
			});
		}
	}

	public static string Hover(Dictionary<string, string> sources, string file, int offset)
	{
		lock (Lock)
		{
			EnsureInitialized();
			var sw = Stopwatch.StartNew();
			var unit = Parse(sources, file);
			if (NodeFinder.perform(unit, offset, 0) is not SimpleName name || name.resolveBinding() is not { } binding)
				return "null";

			var signature = Describe(binding);
			var doc = Javadoc(unit, sources[file], binding);
			var ms = sw.Elapsed.TotalMilliseconds;
			return Json(w =>
			{
				w.WriteNumber("ms", ms);
				w.WriteNumber("start", name.getStartPosition());
				w.WriteNumber("end", name.getStartPosition() + name.getLength());
				w.WriteString("signature", signature);
				if (doc != null)
					w.WriteString("doc", doc);
			});
		}
	}

	private static string Modifiers(int flags)
	{
		var words = new List<string>();
		if (Modifier.isPublic(flags)) words.Add("public");
		if (Modifier.isProtected(flags)) words.Add("protected");
		if (Modifier.isPrivate(flags)) words.Add("private");
		if (Modifier.isAbstract(flags)) words.Add("abstract");
		if (Modifier.isDefault(flags)) words.Add("default");
		if (Modifier.isStatic(flags)) words.Add("static");
		if (Modifier.isFinal(flags)) words.Add("final");
		if (Modifier.isSealed(flags)) words.Add("sealed");
		if (Modifier.isNonSealed(flags)) words.Add("non-sealed");
		return words.Count == 0 ? "" : string.Join(" ", words) + " ";
	}

	private static string TypeKind(ITypeBinding type) =>
		type.isAnnotation() ? "@interface" : type.isInterface() ? "interface" : type.isEnum() ? "enum" : type.isRecord() ? "record" : "class";

	private static string Describe(IBinding binding)
	{
		switch (binding)
		{
			case ITypeBinding type:
				if (type.isTypeVariable() || type.isPrimitive())
					return type.getName();
				var typeParameters = type.getTypeDeclaration().getTypeParameters();
				var generic = typeParameters.Length == 0 ? "" : "<" + string.Join(", ", typeParameters.Select(x => x.getName())) + ">";
				return $"{Modifiers(type.getModifiers())}{TypeKind(type)} {type.getErasure().getQualifiedName()}{generic}";

			case IMethodBinding method:
				var names = method.getParameterNames();
				var parameters = method.getParameterTypes().Select((x, i) => x.getName() + (names != null && i < names.Length ? " " + names[i] : ""));
				var declaring = method.getDeclaringClass();
				var owner = declaring.isAnonymous() ? "" : declaring.getErasure().getQualifiedName() + ".";
				var returns = method.isConstructor() ? "" : method.getReturnType().getName() + " ";
				var methodName = method.isConstructor() ? declaring.getName() : method.getName();
				return $"{Modifiers(method.getModifiers())}{returns}{owner}{methodName}({string.Join(", ", parameters)})";

			case IVariableBinding variable:
				if (variable.isEnumConstant())
					return $"{variable.getDeclaringClass().getQualifiedName()}.{variable.getName()}";
				if (variable.isField())
					return $"{Modifiers(variable.getModifiers())}{variable.getType().getName()} {variable.getDeclaringClass()?.getQualifiedName()}.{variable.getName()}";
				return $"{variable.getType().getName()} {variable.getName()}";

			case IPackageBinding package:
				return $"package {package.getName()}";

			default:
				return binding.getName();
		}
	}

	// only declarations in the file itself have docs: the class library has no sources
	private static string Javadoc(DomUnit unit, string source, IBinding binding)
	{
		if (unit.findDeclaringNode(binding) is not BodyDeclaration { } declaration || declaration.getJavadoc() is not { } javadoc)
			return null;

		var text = source.Substring(javadoc.getStartPosition(), javadoc.getLength());
		text = text.TrimStart('/').TrimStart('*');
		if (text.EndsWith("*/", StringComparison.Ordinal))
			text = text[..^2];
		return string.Join("\n", text.Split('\n').Select(x => Regex.Replace(x, @"^\s*\*\s?", ""))).Trim();
	}

	public static string Complete(Dictionary<string, string> sources, string file, int offset)
	{
		lock (Lock)
		{
			EnsureInitialized();
			var sw = Stopwatch.StartNew();
			environment.Sources = sources
				.Where(x => x.Key != file && x.Key.EndsWith(".java", StringComparison.Ordinal))
				.ToDictionary(x => x.Key[..^".java".Length], x => x.Value);

			var source = sources[file];
			var collector = new ProposalCollector();
			var engine = new CompletionEngine(PlaygroundSearchableEnvironment.Create(environment), collector, options, project, null, null);
			engine.complete(new SourceUnit(source.ToCharArray(), file, "UTF-8"), offset, 0, null);
			var completed = sw.Elapsed.TotalMilliseconds;

			var importAt = ImportOffset(source);
			return Json(w =>
			{
				w.WriteNumber("ms", completed);
				w.WriteStartArray("items");
				foreach (var proposal in collector.Proposals)
				{
					try
					{
						WriteProposal(w, proposal, importAt);
					}
					catch (Exception e)
					{
						// a proposal that wants more of the java model than there is
						Console.Error.WriteLine($"[jdt] skipped proposal {proposal.getKind()} '{new string(proposal.getName() ?? [])}': {e.Message}");
					}
				}
				w.WriteEndArray();
			});
		}
	}

	// where a new import goes: after the last import, or the package declaration
	private static int ImportOffset(string source)
	{
		var last = Regex.Matches(source, @"^[ \t]*(import|package)\s[^;]*;[^\n]*\n?", RegexOptions.Multiline).LastOrDefault();
		return last == null ? 0 : last.Index + last.Length;
	}

	// pseudo members (a type's .class, an array's .length) have no declaring type
	private static string Simple(char[] signature) => signature == null ? null : new string(Signature.getSimpleName(Signature.toCharArray(signature)));

	private static string Detail(string type, string owner) => owner == null ? type : $"{type} - {owner}";

	// parameter names the engine didn't find in source are looked up in the java model, which there isn't. a failed
	// lookup isn't retried, so getCompletion (which wants them for an override) won't fail on it either.
	private static char[][] ParameterNames(CompletionProposal proposal)
	{
		try
		{
			return proposal.findParameterNames(null);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static void WriteProposal(Utf8JsonWriter w, CompletionProposal proposal, int importAt)
	{
		var parameterNames = ParameterNames(proposal);
		var completion = new string(proposal.getCompletion());
		var name = proposal.getName() == null ? null : new string(proposal.getName());
		string kind, label, detail = null, import = null;
		var insert = completion;
		var snippet = false;

		switch (proposal.getKind())
		{
			case CompletionProposal.METHOD_REF:
			case CompletionProposal.METHOD_REF_WITH_CASTED_RECEIVER:
			case CompletionProposal.METHOD_NAME_REFERENCE:
			case CompletionProposal.CONSTRUCTOR_INVOCATION:
			case CompletionProposal.METHOD_DECLARATION:
			{
				var signature = proposal.getSignature();
				var parameterTypes = Signature.getParameterTypes(signature);
				var parameters = parameterTypes.Select((x, i) => Simple(x) + (parameterNames != null && i < parameterNames.Length ? " " + new string(parameterNames[i]) : ""));
				kind = proposal.getKind() == CompletionProposal.CONSTRUCTOR_INVOCATION ? "constructor" : "method";
				label = $"{name}({string.Join(", ", parameters)})";
				detail = Detail(Simple(Signature.getReturnType(signature)), Simple(proposal.getDeclarationSignature()));
				if (parameterTypes.Length > 0 && completion.EndsWith("()", StringComparison.Ordinal))
				{
					insert = completion[..^1] + "$0)";
					snippet = true;
				}
				break;
			}
			case CompletionProposal.FIELD_REF:
			case CompletionProposal.FIELD_REF_WITH_CASTED_RECEIVER:
				kind = (proposal.getFlags() & Flags.AccEnum) != 0 ? "enumMember" : "field";
				label = name;
				detail = Detail(Simple(proposal.getSignature()), Simple(proposal.getDeclarationSignature()));
				break;
			case CompletionProposal.LOCAL_VARIABLE_REF:
				kind = "variable";
				label = name;
				detail = Simple(proposal.getSignature());
				break;
			case CompletionProposal.TYPE_REF:
			{
				var flags = proposal.getFlags();
				kind = Flags.isAnnotation(flags) || Flags.isInterface(flags) ? "interface" : Flags.isEnum(flags) ? "enum" : Flags.isRecord(flags) ? "struct" : "class";
				label = new string(Signature.getSignatureSimpleName(proposal.getSignature()));
				var package = new string(Signature.getSignatureQualifier(proposal.getSignature()));
				detail = package;
				// a type that needs an import is completed qualified: complete it simple, and add the import
				if (completion.Contains('.') && package.Length > 0 && completion == package + "." + label)
				{
					insert = label;
					import = $"import {completion};\n";
				}
				break;
			}
			case CompletionProposal.PACKAGE_REF:
				kind = "module";
				label = completion;
				break;
			case CompletionProposal.KEYWORD:
				kind = "keyword";
				label = completion;
				break;
			default:
				kind = "text";
				label = name ?? completion;
				break;
		}

		w.WriteStartObject();
		w.WriteString("label", label);
		w.WriteString("kind", kind);
		if (detail != null)
			w.WriteString("detail", detail);
		w.WriteString("insert", insert);
		w.WriteString("filter", name ?? label);
		if (snippet)
			w.WriteBoolean("snippet", true);
		w.WriteNumber("start", proposal.getReplaceStart());
		w.WriteNumber("end", proposal.getReplaceEnd());
		w.WriteNumber("relevance", proposal.getRelevance());
		if (import != null)
		{
			w.WriteString("import", import);
			w.WriteNumber("importAt", importAt);
		}
		w.WriteEndObject();
	}

	// the eclipse formatter's built-in profile, indented the way the editor indents. a range formats the lines it touches.
	public static string Format(string source, int start, int end, int tabSize, bool insertSpaces)
	{
		java.util.Map settings;
		lock (Lock)
		{
			EnsureInitialized();
			settings = new java.util.HashMap(options);
		}

		var sw = Stopwatch.StartNew();
		// in the style of the sample program, which formats to itself: cases indented in their switch, empty types
		// kept on one line, and no blank lines forced between them. text blocks are left alone.
		settings.put(DefaultCodeFormatterConstants.FORMATTER_INDENT_SWITCHSTATEMENTS_COMPARE_TO_SWITCH, DefaultCodeFormatterConstants.TRUE);
		foreach (var key in new[] { DefaultCodeFormatterConstants.FORMATTER_KEEP_TYPE_DECLARATION_ON_ONE_LINE, DefaultCodeFormatterConstants.FORMATTER_KEEP_RECORD_DECLARATION_ON_ONE_LINE, DefaultCodeFormatterConstants.FORMATTER_KEEP_ENUM_DECLARATION_ON_ONE_LINE, DefaultCodeFormatterConstants.FORMATTER_KEEP_ANNOTATION_DECLARATION_ON_ONE_LINE })
			settings.put(key, DefaultCodeFormatterConstants.ONE_LINE_IF_EMPTY);
		settings.put(DefaultCodeFormatterConstants.FORMATTER_BLANK_LINES_BEFORE_MEMBER_TYPE, "0");
		settings.put(DefaultCodeFormatterConstants.FORMATTER_TEXT_BLOCK_INDENTATION, DefaultCodeFormatterConstants.INDENT_PRESERVE.ToString());
		settings.put(DefaultCodeFormatterConstants.FORMATTER_TAB_CHAR, insertSpaces ? JavaCore.SPACE : JavaCore.TAB);
		settings.put(DefaultCodeFormatterConstants.FORMATTER_TAB_SIZE, tabSize.ToString());
		settings.put(DefaultCodeFormatterConstants.FORMATTER_INDENTATION_SIZE, tabSize.ToString());

		var formatter = ToolFactory.createCodeFormatter(settings);
		var kind = CodeFormatter.K_COMPILATION_UNIT | CodeFormatter.F_INCLUDE_COMMENTS;
		var newline = source.Contains("\r\n") ? "\r\n" : "\n";
		// null when the source doesn't parse
		var edit = start < 0
			? formatter.format(kind, source, 0, source.Length, 0, newline)
			: formatter.format(kind, source, [new Region(start, end - start)], 0, newline);

		var edits = new List<(int Offset, int Length, string Text)>();
		if (edit != null)
			Flatten(edit, edits);
		var ms = sw.Elapsed.TotalMilliseconds;

		return Json(w =>
		{
			w.WriteNumber("ms", ms);
			w.WriteStartArray("edits");
			foreach (var (offset, length, text) in edits)
			{
				w.WriteStartObject();
				w.WriteNumber("start", offset);
				w.WriteNumber("end", offset + length);
				w.WriteString("text", text);
				w.WriteEndObject();
			}
			w.WriteEndArray();
		});
	}

	private static void Flatten(TextEdit edit, List<(int Offset, int Length, string Text)> edits)
	{
		switch (edit)
		{
			case ReplaceEdit replace:
				edits.Add((replace.getOffset(), replace.getLength(), replace.getText()));
				break;
			case InsertEdit insert:
				edits.Add((insert.getOffset(), 0, insert.getText()));
				break;
			case DeleteEdit delete:
				edits.Add((delete.getOffset(), delete.getLength(), ""));
				break;
		}
		foreach (var child in edit.getChildren())
			Flatten(child, edits);
	}

	private static string Json(Action<Utf8JsonWriter> write)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			write(writer);
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private const string WarmupSource = """
		import java.util.*;

		public class Warmup {
			/** a list */
			static List<String> names = new ArrayList<>();

			public static void main(String[] args) {
				var list = new ArrayList<Integer>();
				list.
				ArrayL
			}
		}
		""";

	// loads, links and jits all three paths, and indexes the class library's types
	public static void Warmup()
	{
		var sources = new Dictionary<string, string> { ["Warmup.java"] = WarmupSource };
		var sw = Stopwatch.StartNew();
		lock (Lock)
			EnsureInitialized();
		var initialized = sw.ElapsedMilliseconds;
		Diagnose(sources, "Warmup.java");
		var parsed = sw.ElapsedMilliseconds;
		Hover(sources, "Warmup.java", WarmupSource.IndexOf("names", StringComparison.Ordinal));
		var hovered = sw.ElapsedMilliseconds;
		Complete(sources, "Warmup.java", WarmupSource.IndexOf("list.", StringComparison.Ordinal) + "list.".Length);
		var members = sw.ElapsedMilliseconds;
		lock (Lock)
			_ = environment.Types;
		var indexed = sw.ElapsedMilliseconds;
		Complete(sources, "Warmup.java", WarmupSource.IndexOf("ArrayL", StringComparison.Ordinal) + "ArrayL".Length);
		var types = sw.ElapsedMilliseconds;
		Console.WriteLine($"[jdt warmup] init {initialized}ms, parse {parsed - initialized}ms, hover {hovered - parsed}ms, complete members {members - hovered}ms, index {environment.Types.Count} types {indexed - members}ms, complete types {types - indexed}ms");
	}
}
