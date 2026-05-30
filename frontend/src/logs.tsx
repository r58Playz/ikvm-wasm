import { css, FC } from "dreamland/core";
import { loglisteners } from "./dotnet";

export function LogView(this: FC<{ scrolling: boolean }>) {
	const create = (color: string, log: string) => {
		const el = document.createElement("div");
		el.classList.add("log");
		el.innerText = log;
		el.style.color = color;
		return el;
	};

	this.cx.mount = () => {
		const logroot = this.root as HTMLElement;
		const frag = document.createDocumentFragment();

		loglisteners.push((x) => frag.append(create(x.color, x.log)));
		setInterval(() => {
			if (frag.children.length > 0) {
				logroot.appendChild(frag);
				logroot.scrollTop = logroot.scrollHeight;
			}
		}, 250);
	};

	return (
		<div
			class="component-log"
			style={this.scrolling ? "overflow: auto" : "overflow: hidden"}
		/>
	);
};
LogView.style = css`
	:scope {
		min-height: 0;
		flex: 1;
		font-family: var(--font-mono);
		white-space: pre-wrap;
		word-break: break-all;
	}

	::-webkit-scrollbar {
		width: 10px;
	}
	::-webkit-scrollbar-track {
		background: var(--surface3);
	}
	::-webkit-scrollbar-thumb {
		background: var(--surface6);
	}
`;
