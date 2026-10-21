# Melodica integration evidence

All compilation and execution ran in a Fedora 44 Incus container on Bak. The
Windows DLL was cross-compiled with pinned Zig 0.16.0 and executed under Wine 11.
No live Pandora login, game injection or host audio output was used.

| Check | Result |
|---|---|
| Linux .NET 10 managed suite, default library resolver | 112 passed, 1 explicit live check skipped |
| Extracted Windows x64 .NET 10 deployment under Wine, default DLL resolver | 112 passed, 1 explicit live check skipped |
| Strict GCC Linux and Windows/Wine shared-library smoke | 1,279 checks each |
| Strict Clang and ASan/UBSan/leak ownership checks | 1,279 checks each |
| Actual Dalamud API 15 plugin Release build | 0 warnings, 0 errors |
| Native package rejection/retention regressions | 6 passed |
| Battery whitespace/style, licenses and vulnerable packages | Passed; no formatting changes, 0 forbidden licenses, 0 reported vulnerabilities |

`deployment-proof.json` records every compiled application and native source
hash, the measured artifact identities, package entries and test results.
`artifacts-all.json` records native compiler versions and artifact hashes.
The source, Wine-loaded, plugin-output and final packaged DLL are byte-identical:

```
aa8e0c8b52e0c84a6eb0b2ef90b650421b7952d9b330fbbd8462bcca0912da39
```

Both native libraries expose exactly ten `xp_*` entry points. Hidden internal
symbols prevent the native smoke test's independent cipher/parser helpers from
interposing on the library's implementation. Native tests call the real DLL.

The managed suite preserves the existing paid-account/MP3, genre, station,
feedback, radio and audio-policy checks while adding transport failure,
cancellation, account-switch, sign-out, unload, malformed metadata and stale
login retry regressions. The Windows run ZIPs and extracts a self-contained test
deployment, unsets the native-library override and resolves the DLL from the
same `runtimes/win-x64/native` location used by the plugin.

The actual plugin ZIP includes the original libpiano MIT notice, third-party
notice and pinned source hashes. It excludes the Linux test library. Its SHA256
and exact DLL comparison are retained in `deployment-proof.json`.

Reproduction commands and ownership rules are in [the bridge contract](../../../native/README.md).
The project retains its experimental status until live account and in-game
acceptance are performed; these offline checks establish the backend integration.
