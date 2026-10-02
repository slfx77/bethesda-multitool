#include <Windows.h>
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved) { (void)reserved; if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module); return TRUE; }
