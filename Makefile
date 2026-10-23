STATICS_RELEASE=fe339c45-059a-44d4-a2de-8b978e25968a
IKVM_RELEASE=5a7617b0-09b3-48d7-bf15-ce93d3bcf78c
DOTNETFLAGS=--nodereuse:false -v n
# the wasm jit runtime is mixed-aot: corlib + ikvm runtime aot'd, everything else interp/jit
AOT?=true
OPT?=false

statics:
	mkdir statics
	wget https://github.com/r58Playz/FNA-WASM-Build/releases/download/$(STATICS_RELEASE)/dotnet-jit.zip -O statics/dotnet.zip
	wget https://github.com/r58Playz/FNA-WASM-Build/releases/download/$(STATICS_RELEASE)/emsdk.zip -O statics/emsdk.zip
	wget https://github.com/r58Playz/IKVM-WASM-Build/releases/download/$(IKVM_RELEASE)/ikvm-wasm-bundle.zip -O statics/ikvm.zip
	unzip -q -o statics/emsdk.zip -d statics/

deps: statics

ikvmc-bundles: deps
	unzip -q -o statics/dotnet.zip -d statics/dotnet
	unzip -q -o statics/ikvm.zip -d statics/ikvm
	python3 build-ikvmc.py all

build: ikvmc-bundles
	rm -r frontend/public/{_framework,image} loader/bin/Release/net10.0/publish/wwwroot/_framework || true
	rm -f frontend/public/assets/rt.jar* || true
#
	dotnet publish loader/IkvmWasm.csproj -c Release -p:IkvmWasmEnableAot=$(AOT) -p:IkvmWasmEnableWasmOpt=$(OPT) $(DOTNETFLAGS)
	cp -r loader/bin/Release/net10.0/publish/wwwroot/_framework frontend/public/
	cp -r statics/ikvm/image frontend/public/
	# dotnet messed up
	sed -i 's/this.appendULeb(32768)/this.appendULeb(65535)/' frontend/public/_framework/dotnet.runtime.*.js
	# event-driven drain of main-thread proxy queue on worker checkMailbox notifications (replaces manual setInterval pump)
	sed -i 's|if (cmd === "checkMailbox") {|if (cmd === "checkMailbox") { if (!ENVIRONMENT_IS_PTHREAD \&\& wasmExports \&\& wasmExports["emscripten_main_thread_process_queued_calls"]) { try { wasmExports["emscripten_main_thread_process_queued_calls"](); } catch (e) {} }|' frontend/public/_framework/dotnet.native.*.js
	cd frontend/public/_framework && split -b20M -d -a1 dotnet.native.*.wasm dotnet.native.*.wasm && rm dotnet.native.*.wasm && split -b20M -d -a1 IKVM.Java.*.dll IKVM.Java.*.dll && rm IKVM.Java.*.dll

serve: build
	cd frontend && pnpm dev

publish: build
	cd frontend && pnpm build

dotnetclean:
	rm -rvf statics/dotnet loader/{bin,obj} loader/Generated.targets loader/ikvmc-manifest.g.json || true
ikvmclean:
	rm -rvf statics/ikvm {statics,jars}/ikvmc_*.{dll,pdb} loader/Generated.targets loader/ikvmc-manifest.g.json || true
clean: dotnetclean ikvmclean
	rm -rvf statics frontend/public/{_framework,image} frontend/public/assets/rt.* || true
