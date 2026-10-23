#include <emscripten.h>
#include <emscripten/em_asm.h>
#include <emscripten/wasmfs.h>
#include <emscripten/html5.h>
#include <emscripten/threading.h>
#include <pthread.h>
#include <stdbool.h>
#include <assert.h>
#include <stdint.h>
#include <unistd.h>
#include <string.h>
#include <sys/stat.h>
#include <stdio.h>
#include <stdlib.h>

extern int managed_em_loop_callback(void);

static void em_loop_wrapper() {
    if (!managed_em_loop_callback()) {
        emscripten_cancel_main_loop();
    }
}

void start_em_loop() {
    emscripten_set_main_loop(em_loop_wrapper, 0, 0);
}

int mount_opfs() {
	backend_t opfs = wasmfs_create_opfs_backend();
	int ret = wasmfs_create_directory("/libsdl", 0777, opfs);
	return ret;
}

backend_t fetch_backend[8] = { NULL };

int mount_fetch(int id, char *srcdir, char *dstdir) {
	fetch_backend[id] = wasmfs_create_fetch_backend(srcdir);
	return wasmfs_create_directory(dstdir, 0777, fetch_backend[id]);
}

int mount_fetch_dir(int id, char *path) {
	if (!fetch_backend[id]) return -1;

	return wasmfs_create_directory(path, 0777, fetch_backend[id]);
}

int mount_fetch_file(int id, char *path) {
	if (!fetch_backend[id]) return -1;

	int ret = wasmfs_create_file(path, 0777, fetch_backend[id]);
	if (ret >= 0)
		return close(ret);
	return ret;
}

void mono_threads_request_thread_dump (void);
EMSCRIPTEN_KEEPALIVE void perform_thread_dump() {
	mono_threads_request_thread_dump();
}

void classloader_debug(char* log) {
    char* copy = strdup(log);
    MAIN_THREAD_ASYNC_EM_ASM({
        console.debug(UTF8ToString($0));
        _free($0);
    }, copy);
}

/* === cross-thread mono log forwarding ===
 *
 * mono's log handler (MONO_LOG_LEVEL/MONO_LOG_MASK output, e.g. "gc", "aot") runs on
 * whichever thread emitted the message and routes to *that* thread's console. Managed
 * code, the GC/finalizer, and the jiterpreter all run on worker threads, so their logs
 * only appear in DevTools' worker consoles — which means you must keep DevTools attached
 * (and eat its slowdown) to see e.g. GC timing. This installs a replacement handler that
 * forwards worker-thread logs to the UI thread's console (async / non-blocking), where the
 * app's console proxy folds them into the in-page LogView. No DevTools, no attach cost.
 *
 * It is inert until you raise the log level: mono only invokes the handler for messages
 * that pass the active MONO_LOG_LEVEL + MONO_LOG_MASK filter, so installing it always is
 * free normally. Note: this only covers mono_trace()-style logging (the mask
 * channels), not raw stdout/printf from native libs. */
typedef void (*MonoLogCallback)(const char *log_domain, const char *log_level, const char *message, int fatal, void *user_data);
extern void mono_trace_set_log_handler (MonoLogCallback callback, void *user_data);
/* the runtime's own JS-backed logger; chaining to it preserves symbolication and the
 * worker fatal-error -> force_exit behaviour for error/critical messages. */
extern void mono_wasm_trace_logger (const char *log_domain, const char *log_level, const char *message, int fatal, void *user_data);

static void ikvm_thread_log_forward (const char *log_domain, const char *log_level, const char *message, int fatal, void *user_data) {
	// keep the runtime's existing behaviour (worker console + fatal handling) intact
	mono_wasm_trace_logger (log_domain, log_level, message, fatal, user_data);

	// the UI thread already logged via the call above; only forward from worker threads
	if (emscripten_is_main_runtime_thread ())
		return;

	int lvl = 0; // 0 log, 1 info, 2 warn, 3 error, 4 debug
	if (log_level) {
		if (!strcmp (log_level, "error") || !strcmp (log_level, "critical")) lvl = 3;
		else if (!strcmp (log_level, "warning")) lvl = 2;
		else if (!strcmp (log_level, "info")) lvl = 1;
		else if (!strcmp (log_level, "debug")) lvl = 4;
	}

	unsigned tid = (unsigned)(uintptr_t)pthread_self ();
	const char *dom = log_domain ? log_domain : "";
	const char *msg = message ? message : "";
	// strdup into the shared heap: the proxied call runs later, after mono may have freed
	// its buffer. The UI thread frees the copy once it has logged it.
	int n = snprintf (NULL, 0, "[t%x][MONO:%s] %s", tid, dom, msg);
	if (n < 0) return;
	char *buf = (char *)malloc ((size_t)n + 1);
	if (!buf) return;
	snprintf (buf, (size_t)n + 1, "[t%x][MONO:%s] %s", tid, dom, msg);

	MAIN_THREAD_ASYNC_EM_ASM ({
		var s = UTF8ToString($0);
		_free($0);
		var c = $1;
		(c == 3 ? console.error : c == 2 ? console.warn : c == 1 ? console.info : c == 4 ? console.debug : console.log)(s);
	}, buf, lvl);
}

EMSCRIPTEN_KEEPALIVE void install_thread_log_forwarder (void) {
	mono_trace_set_log_handler (ikvm_thread_log_forward, NULL);
}
