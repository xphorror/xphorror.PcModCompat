#pragma once

#include <string>

namespace starray::il2cpp_thread_guard {

// Attaches foreign (non-il2cpp) threads to the il2cpp domain before they can
// touch the managed heap. PcCompat mods run managed IL under the embedded
// CoreCLR runtime and call libil2cpp exports directly; a mod worker thread
// that allocates il2cpp objects without being registered with the Boehm GC
// aborts the process with "Collecting from unknown thread". Installing this
// guard instruments the allocation-capable exports with an entry preamble
// that attaches any unregistered thread; the thread is detached again from a
// pthread TLS destructor when it exits.
//
// Idempotent and fail-open: a failure only means foreign threads keep the
// stock (crashy) behavior; UnityMain flow is never blocked.
bool install(std::string &error);

}  // namespace starray::il2cpp_thread_guard
