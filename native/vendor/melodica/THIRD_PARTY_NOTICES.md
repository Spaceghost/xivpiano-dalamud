# libpiano provenance

The historical public API and protocol semantics are based on
[`PromyLOPh/pianobar`](https://github.com/PromyLOPh/pianobar), pinned at
`ad5ec908de10479ecee7d7c0e1030d12146f8f16`. Adapted lifecycle, request and response
logic retains the upstream MIT notice in [LICENSE.libpiano](LICENSE.libpiano).

The native Odin, Zig, Nelua and Codin protocol libraries implement the API in their
respective source languages. Their JSON and cryptographic dependencies are json-c
and libgcrypt. They do not call the C compatibility implementation. Runtime
dependencies and archive contents are recorded separately in comparison evidence;
an archive size does not include its dynamically linked libraries.

Codin accepts the Odin dialect. Its protocol package adapts the native Odin port,
with explicit allocation helpers and compilation through the pinned Codin parser
and this repository's general C99 lowering extension. Compiling the Codin sources
with Odin is a control, not evidence for the Codin C99 artifact.

The portable C compatibility library has its own JSON and Blowfish implementation
and does not link json-c, libgcrypt or libcurl. Tests use those upstream dependencies
as independent oracles.

Pinned language toolchains and the Codin compiler are build tools. Their versions,
checksums and source revisions are recorded by the build scripts; they are not
bundled into installed melodica libraries.
