# JALib Shim Compatibility Contract

## 1. Target

PcModCompat must load PC MOD binaries built against both the JALib `1.0.0.42`
surface used by JipperResourcePack and the local open-source `1.0.0.44` surface.
The production target is the union of those public APIs, not a Jipper-only shim and
not a source-compatible reimplementation of only the latest checkout.

Compatibility has three independently reported levels:

1. `AbiPresent`: type, member signature, generic constraints and required custom
   attributes resolve in the owner ALC.
2. `RuntimeEquivalent`: lifecycle, settings, task, reflection and patch behavior
   produces the same observable MOD result under Android IL2CPP.
3. `ExplicitlyUnavailable`: desktop-only loader, updater or network behavior is
   rejected with a persistent diagnostic; it must not silently no-op.

All satisfiable ABI identities may reach 100%. Exact desktop implementation equivalence is not a
valid target for Harmony transpilers or physical unpatch because Android has no
managed game IL and HookBroker keeps permanent native slots. Those APIs must be
translated to result-equivalent native rules or report `ExplicitlyUnavailable`.

## 2. Audited Baseline

The authoritative ABI baseline is generated from the official JALib `1.0.0.42`
and `1.0.0.44` release assemblies. As of 2026-07-26 the union contains 61 required
public/protected types and 872 required members. The current shim provides:

- types: `61/61` (`100%`);
- members: `871/872` (`99.89%`), equal to the satisfiable union maximum;
- candidate SHA-256:
  `DA87772495AAD29BB39E292FA01611146066EAA79CC4D93E891F7283EA70A157`;
- Jipper binary TypeRef closure: complete;
- JALib directed tests: `42/42`;
- complete managed suite excluding the known Windows-only IL2CPP P/Invoke test:
  `458/458`;
- Android arm64 build: 14 proxy assemblies, 180 exact input types, 191 generated
  types, 14 generic initializers and zero audit issues.

Reference hashes are fixed and must be reported with every regenerated manifest:

- `1.0.0.42`: `A403F5C029FC281E04F54EA7EDB8FEEF082E79D8AF1CC7C010CD0CFE0742F533`;
- `1.0.0.44`: `CB276809C6D1A1953A006426385FE2796FC3C347D0D2619521C0E2F1FB40052B`.

The completed runtime-equivalent mainline now includes owner-aware UnityMain
dispatch, stale-generation rejection, async enable/disable, fixed/update/late
lifecycle, bounded nested coroutines, `Feature`/`MultiFeature` gating, persistent
logical patch registrations, nested settings serialization/disposal, reflection,
unsafe helpers and portable compression/network utility surfaces.

The single unmatched manifest entry is intentional and cannot coexist in one CLR
assembly: `ReversePatchType.AllCombine` is literal `127` in v42 and literal `255`
in v44. Enum literals are one metadata field identity, so publishing either value
necessarily misses the other manifest line. The shim publishes the v44 value `255`,
including `ILManipulateCombine`; all other union types and members are present.

The closure pass added the patch query model and Harmony ordering metadata, exact
JAMod constructor/property/log/report ABI, Unity `SystemLanguage`, persisted custom
language selection, JALib exceptions, reflection-safe `ModReloadCache`, and UMM load
events. `JAMod.DownloadComplete` and `ModTools.ApplyMod` are
`ExplicitlyUnavailable`: both persist a precise diagnostic instead of bypassing the
PcModCompat translator with desktop self-update or runtime DLL loading.

ABI presence does not imply that a desktop Harmony operation is executable on
IL2CPP. Query objects must describe the actual logical HookBroker registry. A
registered but untranslated patch remains `registered_only` or `unsupported`;
it must never be reported as active.

## 3. Hook Semantics

JALib `Patch/Unpatch`, feature enable/disable and MOD enable/disable never install or
remove physical hooks from managed code. They publish owner and feature generations
to HookBroker. A disabled feature must stop receiving callbacks immediately while
the native slot remains installed. Re-enabling activates a new generation and must
not replay stale queued callbacks.

Patch support is capability-based:

- statically or dynamically registered callbacks retain exact target overload and
  hidden `MethodInfo*` forwarding requirements;
- known Prefix/Postfix callbacks may use generated managed dispatch or compiled
  native rule bytecode;
- ReversePatch uses a generic verified state bridge where possible, not a fabricated
  managed game body;
- Transpiler/Finalizer/Replace/Override require proven translation; unsupported IL
  fails import or activation with the precise member and reason.

## 4. Thread And Lifecycle Semantics

All Unity API work runs on PcCompat UnityMain. `MainThread.Run`, JATask
continuations and coroutine resumes carry owner, feature and session generation.
Disable/unload drops stale work. Exceptions fault only the owning MOD/feature and are
persisted to diagnostics.

The required lifecycle order is:

`Setup -> Patch registration -> Enable -> Update/Fixed/Late -> Disable -> Unload`.

Feature activation patches before `OnEnable`; logical deactivation gates callbacks
before `OnDisable`. MultiFeature reference counts shared patch state across child
features.

## 5. Settings And Mobile Presentation

Original JALib `OnGUI/SettingGUI` remains the truth source. The mobile host may adapt
geometry and input ownership but must preserve callback order, values and saves.

The host has one IME owner: `None`, `ModManager` or `UnitySettings`. Owner changes
clear the previous UI system's focused control before the new owner can request the
keyboard. Dear ImGui ActiveID, Unity `GUIUtility.keyboardControl` and Android IME
visibility are separate states and must never be inferred from one another.

Mobile layout requirements:

- `TouchHeight` is applied as a real minimum control height, not diagnostics only;
- only wrapping labels may use word wrap; buttons, toggles and text fields do not;
- long label/control rows may stack vertically; enum choices wrap into rows;
- title/footer have fixed budgets outside one scroll viewport;
- every Repaint diagnostic records actual control rectangles and flags clipping,
  overlap and out-of-panel rectangles.

Settings font ownership follows the active MOD resource session. The host resolves
one unique exact `UnityEngine.Font` from the owner-scoped VirtualBundle first. If no
legacy Font exists, one unique static `TMPro.TMP_FontAsset` may be projected from its
Resource IR atlas, glyph metrics and Material into a Unity 6 TextCore FontAsset. A
private legacy Font is only the `GUIStyle.font` identity key. The game language's
`RDString.fontData.font` is the final fallback, not the primary source.

Projection never mutates the MOD's TMP font, atlas, material or HUD. Unity 6000
IMGUI's metadata-resolved `TextSettings.GetCachedFontAsset(Font)` entry is retained
permanently by HookBroker; mapped private Font identities return their owner-scoped
TextCore FontAsset, while every other Font forwards to the original with the hidden
`MethodInfo*` unchanged. The mapping and objects are retired before source resources
when the owner generation ends and are reported as `fontSource=VirtualBundle`.
Ambiguous candidates, type mismatches, missing hooks and reconstruction failures fail
closed and report a bounded Logcat summary plus the complete exported diagnostic.

## 6. Build Gates

The build must generate and compare API manifests for JALib v42/v44 and the shim.
Closure checks include TypeRef, MemberRef signature, fields/properties/events,
generic constraints, base types and required attributes. Real Jipper remains a
runtime regression fixture, but no single MOD may define the accepted API surface.

Release reporting lists ABI coverage, runtime-equivalent coverage and explicitly
unavailable members separately. A missing member is a build failure except for the
single pinned `ReversePatchType.AllCombine=127` v42/v44 literal conflict. The build
gate must require exactly that one mismatch; any additional missing entry fails.
Unsupported semantic paths remain import/activation failures or persistent runtime
diagnostics according to the capability contract.

This gate is enforced by `build_shims.ps1` through `JALibApiManifest verify` and is
therefore inherited by Android and top-level builds. It pins reference versions,
the `61/872` union dimensions and the exact single allowed missing member. The full
current report is written to `out/api/JALib-shim-coverage.json`; failed builds print
only a bounded preview while preserving every mismatch in that JSON file.
