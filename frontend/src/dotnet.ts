import type { ModuleAPI, MonoConfig, RuntimeAPI } from "./dotnetdefs";

const wasm: ModuleAPI = await eval(`import("/_framework/dotnet.js")`);
const dotnet = wasm.dotnet;
let runtime: RuntimeAPI;
let config: MonoConfig;
let exports: any;
let warmup: Promise<void>;

export type Log = { color: string; log: string };
export let loglisteners: ((log: Log) => void)[] = [];

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
		// ikvm's own stats, which it always prints in the browser
		if (/^\[IKVM (lazybody|indy|privileged)\]/.test(str)) return;
		for (const logger of loglisteners) {
			logger({ color, log: str });
		}
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

export async function initDotnet() {
	// emscripten proxy hackfix number 39847232303
	(globalThis as any).Atomics.waitAsync = undefined;

	console.time("dotnet ");
	let start = performance.now();
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
		// no parallel collectors (major=marksweep-conc-par,minor=simple-par): their gc worker threads take up the
		// pthread pool, and the main thread then deadlocks creating the finalizer thread
		.withEnvironmentVariable("MONO_GC_PARAMS", "nursery-size=16m,tlab-size=64k,default-allowance-ratio=2.0")
		// cap threadpools
		.withEnvironmentVariable("DOTNET_PROCESSOR_COUNT", "4")

		// wasm jit tier: on by default (MONO_WASM_JIT_AUTO=0 turns it off), STATS=1 prints what it compiled
		.withEnvironmentVariable("MONO_WASM_JIT_RAISE_NOGC", "1")
		//.withEnvironmentVariable("MONO_WASM_JIT_STATS", "1")

		// openjdk-style lazy linking in ikvm
		.withEnvironmentVariable("IKVM_LAZY_CLASS_LOADING", "1")
		.withEnvironmentVariable("IKVM_LAZY_INPLACE", "1")
		.withEnvironmentVariable("IKVM_LAZY_SIG", "1")
		//.withEnvironmentVariable("IKVM_FROMCLASS_TRACE", "1")
		//.withEnvironmentVariable("IKVM_UNSAFE_OFFSET_TRACE", "1")
		.withRuntimeOptions([
			// accept smaller traces earlier
			`--jiterpreter-minimum-trace-value=${10}`,
			`--jiterpreter-minimum-trace-hit-count=${1000}`,
			`--jiterpreter-back-branch-boost=${980}`, // make sure this is below trace hit count
			`--jiterpreter-minimum-distance-between-traces=${3}`,
			`--jiterpreter-trace-monitoring-period=${500}`,
			`--jiterpreter-trace-monitoring-max-average-penalty=${75}`,

			// increase jit function limits. every method the wasm jit tier compiles takes 2 table slots,
			// and it silently stops compiling once the table is full
			`--jiterpreter-wasm-bytes-limit=${64 * 1024 * 1024}`,
			`--jiterpreter-max-module-size=${64 * 1024 - 1}`,
			`--jiterpreter-table-size=${128 * 1024}`,
			`--jiterpreter-aot-table-size=${64 * 1024}`,

			// print jit stats
			//`--jiterpreter-stats-enabled`,

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
	await exports.IkvmWasm.PreInit(location.origin, []);
	let end = performance.now()
	console.debug(`dotnet initialized ${(end - start).toFixed(0)}`);
	console.timeEnd("dotnet ");

	// load and jit the compiler in the background. failures are logged by the runtime, and runJava will report its own
	warmup = exports.IkvmWasm.Warmup().catch(() => {});
}

export async function runJava(source: Map<string, string>) {
	await warmup;
	console.time("runJava ");
	await exports.IkvmWasm.RunJava([...source.entries()].map(([a, b]) => {
		if (a.endsWith(".java"))
			a = a.slice(0, a.length - ".java".length);
		return [a, b];
	}));
	console.timeEnd("runJava ");
}
