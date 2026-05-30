import { css, type FC } from "dreamland/core";

import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

(self as any).MonacoEnvironment = {
	getWorker(_: any, label: any) {
		return new editorWorker();
	},
};

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
