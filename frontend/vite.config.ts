import { defineConfig, type Plugin, type ViteDevServer } from "vite";
import basicSsl from "@vitejs/plugin-basic-ssl";
import { server as wisp, logging as wispLogging } from "@mercuryworkshop/wisp-js/server";

wispLogging.set_level(wispLogging.WARN);

function localWisp(): Plugin {
	const attach = (httpServer: ViteDevServer["httpServer"]) => {
		httpServer?.on("upgrade", (req, socket, head) => {
			if (req.url?.startsWith("/wisp/")) wisp.routeRequest(req, socket, head);
		});
	};
	return {
		name: "local-wisp",
		configureServer(server) {
			attach(server.httpServer);
		},
		configurePreviewServer(server) {
			attach(server.httpServer);
		},
	};
}

export default defineConfig({
	plugins: [basicSsl(), localWisp()],
	build: {
		target: "es2022",
		rollupOptions: {
			output: {
				manualChunks: (id) => {
					if (id.includes("monaco-editor")) {
						return "monaco";
					}
				},
			},
		},
	},
	server: {
		headers: {
			"Cross-Origin-Opener-Policy": "same-origin",
			"Cross-Origin-Embedder-Policy": "require-corp",
		},
		port: 5023,
		strictPort: true,
		host: true,
		allowedHosts: ["nyatop.internal.hgci.org", "100.64.0.10"]
	},
});
