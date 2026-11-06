Umbra
=====

A Windows 11 taskbar built to minimise OLED burn-in, on any screen edge.

  https://github.com/alharethh/Umbra


REQUIREMENTS

  Windows 11.

  This download needs the .NET 10 Desktop Runtime:
  https://dotnet.microsoft.com/download/dotnet/10.0  (choose "Desktop Runtime", x64)

  If Umbra does not start, that runtime is what is missing. The one-file .exe download
  carries its own copy and needs nothing installed.


RUNNING IT

  Umbra.exe

  The release is code-signed, but Windows trusts a new publisher only over time, so it may
  still stop the first launch with "Windows
  protected your PC". Choose More info, then Run anyway.

  Umbra replaces Explorer's taskbar while it runs. Right-click an empty part of the bar for
  Settings, the wear heatmap, and Exit Umbra.

  Settings live in %LOCALAPPDATA%\Umbra\settings.txt and apply immediately, except hosting
  the notification area, which needs a restart. Umbra starts at sign-in only if you tick
  "Start Umbra when I sign in" in Settings.


GETTING YOUR WINDOWS TASKBAR BACK

  For now: right-click the bar, Exit Umbra. Your normal taskbar comes back as it was.

  To remove Umbra completely, in this order:

    1. Right-click the bar, Settings, untick "Start Umbra when I sign in".
    2. Right-click the bar, Exit Umbra, and check your normal taskbar is back.
    3. Delete this folder.
    4. Delete %LOCALAPPDATA%\Umbra (paste it into File Explorer's address bar). It holds
       your settings, pins, wear history and a log. Do this last, and never while your
       taskbar is missing: while Umbra runs, or after it crashed, it also holds the note of
       how your taskbar was set up, which is how Umbra puts it back.

  If you deleted Umbra without step 1, the leftover sign-in entry does nothing. Task Manager
  (Ctrl+Shift+Esc), Startup apps, Umbra, Disable silences it.

  If Umbra crashed or was closed by force and your taskbar is missing:

    - Best: start Umbra.exe again, then right-click the bar, Exit Umbra. It notices the
      last session did not end properly and puts your taskbar back exactly as it was,
      including where its icons sit and whether it hides itself. Win+E still opens File
      Explorer without a taskbar.
    - Or restart Windows Explorer: Ctrl+Shift+Esc, find "Windows Explorer" under Processes,
      Restart. Signing out and back in does the same. This way puts back neither of the two
      settings Umbra changes, so check both in Windows Settings (Win+I) > Personalization >
      Taskbar > Taskbar behaviors:
        - Its icons will be on the left, because Umbra moves them there while it runs. If
          you had them centred (the Windows 11 default), set Taskbar alignment to Center.
        - It may hide itself. If so, untick Automatically hide the taskbar.
      Starting Umbra once more and choosing Exit Umbra also fixes both, as long as
      %LOCALAPPDATA%\Umbra is still there.

  Pressing the Windows key on its own does not bring the taskbar back in that state.


KNOWN LIMITS

  - A bar on every monitor exists but is off by default, and is all or nothing: the OLED
    treatment cannot yet be applied to one screen and not another.
  - No keyboard navigation. Screen readers can hear the bar's names but cannot operate it.
  - The notification area keeps a timer running ten times a second to hold its window in
    front of Explorer's. It does not stop the display idling; it costs some power. Turn the
    notification area off in Settings if you would rather not pay it.


LICENCE

  MIT -- see LICENSE. ManagedShell is Apache 2.0 -- see THIRD-PARTY-NOTICES.md.
