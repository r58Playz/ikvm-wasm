import { css, FC } from "dreamland/core";
import "./style.css";
import { initDotnet, runJava } from "./dotnet";
import { LogView } from "./logs";
import { Monaco } from "./monaco";

let DEFAULT_CODE = `package com.example;

public class Main {
	public static void main(String[] args) {
		System.out.println("Hi from Java -> IKVM -> Mono -> WASM");
	}
}
`;

function GitHub(this: FC<{}>) {
	return (
		<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M12,2A10,10 0 0,0 2,12C2,16.42 4.87,20.17 8.84,21.5C9.34,21.58 9.5,21.27 9.5,21C9.5,20.77 9.5,20.14 9.5,19.31C6.73,19.91 6.14,17.97 6.14,17.97C5.68,16.81 5.03,16.5 5.03,16.5C4.12,15.88 5.1,15.9 5.1,15.9C6.1,15.97 6.63,16.93 6.63,16.93C7.5,18.45 8.97,18 9.54,17.76C9.63,17.11 9.89,16.67 10.17,16.42C7.95,16.17 5.62,15.31 5.62,11.5C5.62,10.39 6,9.5 6.65,8.79C6.55,8.54 6.2,7.5 6.75,6.15C6.75,6.15 7.59,5.88 9.5,7.17C10.29,6.95 11.15,6.84 12,6.84C12.85,6.84 13.71,6.95 14.5,7.17C16.41,5.88 17.25,6.15 17.25,6.15C17.8,7.5 17.45,8.54 17.35,8.79C18,9.5 18.38,10.39 18.38,11.5C18.38,15.32 16.04,16.16 13.81,16.41C14.17,16.72 14.5,17.33 14.5,18.26C14.5,19.6 14.5,20.68 14.5,21C14.5,21.27 14.66,21.59 15.17,21.5C19.14,20.16 22,16.42 22,12A10,10 0 0,0 12,2Z"></path></svg>
	)
}
GitHub.style = css`
	:scope {
		height: 1.25em;
		aspect-ratio: 1 / 1;
		fill: currentcolor;
	}
`;

function App(this: FC<{}, { value: string, ready: boolean }>) {
	this.ready = false;
	this.value = DEFAULT_CODE;
	this.cx.mount = async () => {
		console.debug("initializing dotnet");
		await initDotnet();
		this.ready = true;
	};

	let run = async () => {
		if (this.ready) {
			this.ready = false;
			let failed = false;
			let start = performance.now();
			try {
				await runJava(new Map([["Main.java", this.value]]));
			} catch (err) {
				failed = true;
			}
			let end = performance.now();
			console.info(`${failed ? "Failed" : "Finished"} in ${((end - start) / 1000).toFixed(2)}s`);
			this.ready = true;
		}
	}

	return (
		<div>
			<div class="toolbar">
				<span>Java Compiler <span class="sub">via IKVM + Mono-WASM</span></span>
				<a href="https://github.com/r58Playz/ikvm-wasm" target="_blank"><GitHub /></a>
				<div class="expand" />
				<button on:click={run} disabled={use(this.ready).not()}>Run</button>
			</div>
			<Monaco name="Main.java" value={use(this.value)} />
			<LogView scrolling={true} />
		</div>
	)
}
App.style = css`
	:scope {
		height: 100%;
		background: var(--surface0);
		color: var(--fg);
		font-family: var(--font-body);

		display: grid;
		grid-template-rows: min-content 1fr;
		grid-template-columns: 1fr 1fr;
		grid-template-areas:
			"toolbar toolbar"
			"monaco logs";
		gap: 0.5rem;
	}

	:global(.monaco) {
		grid-area: monaco;
	}

	.toolbar {
		grid-area: toolbar;
		font-size: 1.75rem;
		padding: 0.5rem 1rem 0 1rem;

		display: flex;
		align-items: center;
		gap: 1rem;
	}

	.toolbar .sub {
		font-size: 1.25rem;
		color: var(--fg2);
	}

	.toolbar a {
		color: var(--accent);
		display: flex;
		align-items: center;
	}

	.toolbar button {
		background: var(--accent);
		color: var(--bg);
		border: 0px solid;
		border-radius: 20rem;
		padding: 0.25rem 1rem;
		font-size: 0.75em;
		font-family: var(--font-body);

		transition: background 0.15s, color 0.15s;
		cursor: pointer;
	}

	.toolbar button:disabled {
		background: var(--surface4);
		color: var(--fg);
		cursor: not-allowed;
	}

	:global(.component-log) {
		grid-area: logs;
		align-self: stretch;
		background: var(--surface1);

		font-size: 0.9rem;
	}

	.expand { flex: 1; }

	@media (max-width: 1000px) {
		:scope {
			grid-template-rows: min-content 1fr 1fr;
			grid-template-columns: 1fr;
			grid-template-areas:
				"toolbar"
				"monaco"
				"logs";
		}
	}
`;

document.querySelector("#app")!.replaceWith(<App />);
