import type { ModuleAPI, MonoConfig, RuntimeAPI } from "./dotnetdefs";

const wasm: ModuleAPI = await eval(`import("/_framework/dotnet.js")`);
const dotnet = wasm.dotnet;
let runtime: RuntimeAPI;
let config: MonoConfig;
let exports: any;

export type Log = { color: string; log: string };
export let loglisteners: ((log: Log) => void)[] = [];

let logs: string[] = [];
(globalThis as any).logs = logs;

function proxyConsole(name: string, color: string) {
	// @ts-expect-error ts sucks
	const old = console[name].bind(console);
	// @ts-expect-error ts sucks
	console[name] = (...args) => {
		let str;
		try {
			str = args.join(" ");
		} catch {
			str = "<failed to render>";
		}
		if (str.includes("maybeExit:") || str.includes("runtimeKeepalive")) return;
		old(...args);
		for (const logger of loglisteners) {
			logger({ color, log: str });
		}
		logs.push(str);
	};
	return old;
}
export const bypassError = proxyConsole("error", "var(--error)");
export const bypassWarn = proxyConsole("warn", "var(--warning)");
export const bypassLog = proxyConsole("log", "var(--fg)");
export const bypassInfo = proxyConsole("info", "var(--info)");
export const bypassDebug = proxyConsole("debug", "var(--fg4)");
(globalThis as any).bypassLog = bypassLog;

async function joinSplit(baseUri: string) {
	let idx = 0;

	let fetchNext = async () => {
		let res = await fetch(baseUri + idx);
		idx++;
		if (!res.body) throw new Error("no body in fetch response");
		return res.status === 200 && !(res.headers.get("content-type") || "").includes("text/html")
			? res.body.getReader()
			: null;
	};

	let chunk = await fetchNext();
	if (!chunk) throw new Error("failed to fetch first chunk");
	let currentStream: ReadableStreamDefaultReader<Uint8Array> = chunk;

	let stream = new ReadableStream({
		async pull(controller) {
			let { value, done } = await currentStream.read();
			if (done || !value) {
				chunk = await fetchNext();

				if (chunk) {
					currentStream = chunk;
					await this.pull!(controller);
				} else {
					controller.close();
				}
			} else {
				controller.enqueue(value);
			}
		},
	});

	return stream;
}

async function resourceLoader(defaultUri: string, contentType: string) {
	let stream = await joinSplit(defaultUri);
	let res = new Response(stream, {
		headers: new Headers({ "Content-Type": contentType }),
	});
	return res;
}

export async function maybeDownloadRtJar() {
	let dir = await navigator.storage.getDirectory();
	try {
		await dir.getFileHandle("rt.jar", { create: false });
		return;
	} catch { }
	console.debug("downloading rt.jar");
	let file = await dir.getFileHandle("rt.jar", { create: true });
	let writable = await file.createWritable();
	let stream = await joinSplit("/assets/rt.jar");
	await stream.pipeTo(writable);
}

export async function initDotnet() {
	// emscripten proxy hackfix number 39847232303
	(globalThis as any).Atomics.waitAsync = undefined;

	console.time("dotnet ");
	runtime = await dotnet
		.withConfig({ pthreadPoolInitialSize: 4 })
		.withResourceLoader((type, _name, defaultUri, _integrity, behavior) => {
			// since aot'd wasm and ikvm.java are >20mb
			if (type === "dotnetwasm" && behavior === "dotnetwasm") {
				return resourceLoader(defaultUri, "application/wasm")
			} else if (type === "assembly" && behavior === "assembly" && defaultUri.includes("IKVM.Java.")) {
				return resourceLoader(defaultUri, "application/octet-stream");
			}
		})
		.withModuleConfig({
			onRuntimeInitialized(Module: any) {
				(globalThis as any).wasm = { Module, FS: Module.FS };
			}
		})
		.withEnvironmentVariable("MONO_SLEEP_ABORT_LIMIT", "20000")
		//.withEnvironmentVariable("MONO_LOG_LEVEL", "debug")
		//.withEnvironmentVariable("MONO_LOG_MASK", "gc")
		//.withEnvironmentVariable("MONO_LOG_MASK", "aot")
		.withEnvironmentVariable("MONO_GC_PARAMS", "nursery-size=16m")
		//.withEnvironmentVariable("IKVM_FROMCLASS_TRACE", "1")
		//.withEnvironmentVariable("IKVM_UNSAFE_OFFSET_TRACE", "1")
		.withRuntimeOptions([
			// accept smaller traces earlier
			`--jiterpreter-minimum-trace-value=${10}`,
			`--jiterpreter-minimum-trace-hit-count=${1000}`,
			`--jiterpreter-back-branch-boost=${980}`, // make sure this is below trace hit count
			`--jiterpreter-minimum-distance-between-traces=${3}`,
			`--jiterpreter-trace-monitoring-period=${500}`,
			`--jiterpreter-trace-monitoring-max-average-penalty=${50}`,

			// increase jit function limits
			`--jiterpreter-wasm-bytes-limit=${64 * 1024 * 1024}`,
			`--jiterpreter-max-module-size=${64 * 1024 - 1}`,
			`--jiterpreter-table-size=${32 * 1024}`,

			// print jit stats
			`--jiterpreter-stats-enabled`,

			//`--no-jiterpreter-jit-call-enabled`,
			//`--no-jiterpreter-interp-entry-enabled`,

			//`--no-jiterpreter-traces-enabled`
		])
		.create();

	config = runtime.getConfig();
	exports = await runtime.getAssemblyExports(config.mainAssemblyName!);

	(globalThis as any).wasm = {
		Module: runtime.Module,
		FS: (runtime.Module as any).FS,
		dotnet,
		runtime,
		config,
		exports,
	};
	console.debug("PreInit...");
	await runtime.runMain();
	await exports.IkvmWasm.PreInit(location.href, []);
	console.debug("dotnet initialized");
	console.timeEnd("dotnet ");
}

export async function runJava(source: Map<string, string>) {
	console.time("runJava ");
	await exports.IkvmWasm.RunJava([...source.entries()].map(([a, b]) => {
		if (a.endsWith(".java"))
			a = a.slice(0, a.length - ".java".length);
		return [a, b];
	}));
	console.timeEnd("runJava ");
}
