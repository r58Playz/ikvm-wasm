# ikvm-wasm

Demo frontend showcasing Java 25 code compiled (with ECJ) and run in the browser via IKVM on mono-wasm, using a custom
fork of mono with a wasm JIT tier.

## Building
1. Install the .NET 10 SDK with the wasm-tools workload
2. Make sure you have `split` `unzip` `wget` `python3` `sed`
3. `make serve` to serve, `make publish` to build for release
