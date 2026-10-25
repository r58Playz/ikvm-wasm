import { css, type FC } from "dreamland/core";

import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import { complete, diagnose, format, hover } from "./dotnet";

(self as any).MonacoEnvironment = {
	getWorker() {
		return new editorWorker();
	},
};

const fileOf = (model: monaco.editor.ITextModel) => model.uri.path.split("/").pop()!;
// every java file open in the editor, by name
const sources = () => new Map(monaco.editor.getModels().filter((x) => x.getLanguageId() === "java").map((x) => [fileOf(x), x.getValue()]));

const kinds: Record<string, monaco.languages.CompletionItemKind> = {
	method: monaco.languages.CompletionItemKind.Method,
	constructor: monaco.languages.CompletionItemKind.Constructor,
	field: monaco.languages.CompletionItemKind.Field,
	enumMember: monaco.languages.CompletionItemKind.EnumMember,
	variable: monaco.languages.CompletionItemKind.Variable,
	class: monaco.languages.CompletionItemKind.Class,
	interface: monaco.languages.CompletionItemKind.Interface,
	enum: monaco.languages.CompletionItemKind.Enum,
	struct: monaco.languages.CompletionItemKind.Struct,
	module: monaco.languages.CompletionItemKind.Module,
	keyword: monaco.languages.CompletionItemKind.Keyword,
	text: monaco.languages.CompletionItemKind.Text,
};

monaco.languages.registerCompletionItemProvider("java", {
	triggerCharacters: ["."],
	async provideCompletionItems(model, position) {
		let offset = model.getOffsetAt(position);
		let items = await complete(sources(), fileOf(model), offset);
		let word = model.getWordUntilPosition(position);
		let fallback = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, position.column);

		return {
			suggestions: items.map((item) => {
				// monaco wants a range on the cursor's line that contains it
				let start = model.getPositionAt(item.start);
				let end = model.getPositionAt(Math.max(item.end, offset));
				let range = item.start <= offset && start.lineNumber === position.lineNumber && end.lineNumber === position.lineNumber
					? monaco.Range.fromPositions(start, end)
					: fallback;
				let importAt = item.import ? model.getPositionAt(item.importAt!) : null;

				return {
					label: { label: item.label, description: item.detail },
					kind: kinds[item.kind] ?? monaco.languages.CompletionItemKind.Text,
					insertText: item.insert,
					insertTextRules: item.snippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
					filterText: item.filter,
					sortText: String(100000 - item.relevance).padStart(6, "0"),
					range,
					additionalTextEdits: importAt ? [{ range: monaco.Range.fromPositions(importAt, importAt), text: item.import! }] : undefined,
				};
			}),
		};
	},
});

monaco.languages.registerHoverProvider("java", {
	async provideHover(model, position) {
		let info = await hover(sources(), fileOf(model), model.getOffsetAt(position));
		if (!info) return null;

		return {
			range: monaco.Range.fromPositions(model.getPositionAt(info.start), model.getPositionAt(info.end)),
			contents: [{ value: "```java\n" + info.signature + "\n```" }, ...(info.doc ? [{ value: info.doc }] : [])],
		};
	},
});

async function formatEdits(model: monaco.editor.ITextModel, range: monaco.IRange | null, options: monaco.languages.FormattingOptions) {
	let offsets = range && { start: model.getOffsetAt({ lineNumber: range.startLineNumber, column: range.startColumn }), end: model.getOffsetAt({ lineNumber: range.endLineNumber, column: range.endColumn }) };
	let edits = await format(model.getValue(), offsets, options.tabSize, options.insertSpaces);
	return edits.map((edit) => ({
		range: monaco.Range.fromPositions(model.getPositionAt(edit.start), model.getPositionAt(edit.end)),
		text: edit.text,
	}));
}

monaco.languages.registerDocumentFormattingEditProvider("java", {
	provideDocumentFormattingEdits: (model, options) => formatEdits(model, null, options),
});

monaco.languages.registerDocumentRangeFormattingEditProvider("java", {
	provideDocumentRangeFormattingEdits: (model, range, options) => formatEdits(model, range, options),
});

const severities = {
	error: monaco.MarkerSeverity.Error,
	warning: monaco.MarkerSeverity.Warning,
	info: monaco.MarkerSeverity.Info,
};

// problems as you type, a moment after you stop
function checkProblems(model: monaco.editor.ITextModel) {
	let timer: ReturnType<typeof setTimeout> | undefined;
	let check = () => {
		clearTimeout(timer);
		timer = setTimeout(async () => {
			let version = model.getVersionId();
			let problems = await diagnose(sources(), fileOf(model));
			if (model.isDisposed() || model.getVersionId() !== version) return;

			monaco.editor.setModelMarkers(model, "jdt", problems.map((problem) => {
				let start = model.getPositionAt(problem.start);
				let end = model.getPositionAt(Math.max(problem.end, problem.start + 1));
				return {
					severity: severities[problem.severity],
					message: problem.message,
					code: String(problem.id),
					startLineNumber: start.lineNumber,
					startColumn: start.column,
					endLineNumber: end.lineNumber,
					endColumn: end.column,
				};
			}));
		}, 300);
	};
	model.onDidChangeContent(check);
	check();
}

export function Monaco(this: FC<{ name: string; value: string; }>) {
	let register = async (model: monaco.editor.IModel) => {
		let recompile = async () => {
			setting = true;
			this.value = model.getValue();
		};

		let setting = false;
		model.onDidChangeContent(recompile);
		use(this.value).listen((x) => {
			if (!setting) model.setValue(x);
		});

		await recompile();
	};

	this.cx.mount = () => {
		let editor = monaco.editor.create(this.root, {
			model: monaco.editor.createModel(
				this.value,
				"java",
				monaco.Uri.file(this.name)
			),
			automaticLayout: true,
			theme: "vs-dark",
			fontFamily: `"Adwaita Mono", "IBM Plex Mono", ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace`,
			fontSize: 14.4,
		});
		let model = editor.getModel()!;

		register(model);
		checkProblems(model);
	};

	return <div class="monaco" />;
}
Monaco.style = css`
	:scope {
		width: 100%;
		height: 100%;
	}

	:scope > :global(.monaco-editor) {
		position: absolute;
	}
`;
