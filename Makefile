STATICS_RELEASE=2f78bbde-8957-4985-87c8-05232cd3d844
IKVM_RELEASE=eddbf576-97e4-49fa-8606-f6c6f6f9b8ae
DOTNETFLAGS=--nodereuse:false -v n
AOT?=false
OPT?=false

RT_JAR_SRC := $(firstword \
	$(wildcard $(JAVA_HOME)/jre/lib/rt.jar) \
	$(wildcard /usr/lib/jvm/java-8-openjdk/jre/lib/rt.jar))

statics:
	mkdir statics
	wget https://github.com/r58Playz/FNA-WASM-Build/releases/download/$(STATICS_RELEASE)/dotnet.zip -O statics/dotnet.zip
	wget https://github.com/r58Playz/FNA-WASM-Build/releases/download/$(STATICS_RELEASE)/emsdk.zip -O statics/emsdk.zip
	wget https://github.com/r58Playz/IKVM-WASM-Build/releases/download/$(IKVM_RELEASE)/ikvm-wasm-bundle.zip -O statics/ikvm.zip
	unzip -q -o statics/emsdk.zip -d statics/

frontend/public/assets/rt.jar0:
	cp $(RT_JAR_SRC) frontend/public/assets/rt.jar
	cd frontend/public/assets && split -b20M -d -a1 rt.jar rt.jar && rm rt.jar

deps: statics frontend/public/assets/rt.jar0

ikvmc-bundles: deps
	unzip -q -o statics/dotnet.zip -d statics/dotnet
	unzip -q -o statics/ikvm.zip -d statics/ikvm
	python3 build-ikvmc.py all

build: ikvmc-bundles
	rm -r frontend/public/{_framework,ikvm} loader/bin/Release/net10.0/publish/wwwroot/_framework || true
#
	./aotprofile.sh statics/ikvm_java.aotprofile statics/ikvm/IKVM.Java.dll \
		ikvm.runtime. ikvm.internal. \
		java.lang. java.util. java.nio. java.net. java.security. java.time. \
		sun.nio.fs. sun.nio.cs. com.sun.nio.zipfs. \
		sun.reflect. sun.misc.
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
	rm -rvf loader/{bin,obj} loader/Generated.targets loader/ikvmc-manifest.g.json || true
ikvmclean:
	rm -rvf {statics,jars}/ikvmc_*.{dll,pdb} {statics,jars}/*.aotprofile loader/Generated.targets loader/ikvmc-manifest.g.json || true
clean: dotnetclean ikvmclean
	rm -rvf statics frontend/public/assets/rt.* || true
