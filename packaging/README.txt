Umbra 0.1.0
===========

A Windows 11 taskbar that lives on any edge, built to minimise OLED burn-in.

  https://github.com/alharethh/Umbra


REQUIREMENTS

  Windows 11.

  The framework-dependent download also needs the .NET 10 Desktop Runtime:
  https://dotnet.microsoft.com/download/dotnet/10.0  (choose "Desktop Runtime", x64)

  If Umbra does not start, that runtime is what is missing. The self-contained download
  carries its own copy and needs nothing installed.


RUNNING IT

  Umbra.exe

  Umbra hides Explorer's taskbar while it runs and puts it back when it exits -- including
  after a crash, which it recovers from on the next launch.

  Right-click the bar for Settings, the wear heatmap, and Exit. There is no other way to
  close it: the bar has no window of its own to close.

  Settings live in %LOCALAPPDATA%\Umbra\settings.txt and mostly apply immediately. Only
  hosting the notification area needs a restart.


KNOWN LIMITS

  - One monitor at a time. You choose which; you cannot have a bar on each.
  - No keyboard navigation and no screen-reader support yet.
  - The notification area keeps a timer running ten times a second, which is how it holds
    its window in front of Explorer's. A screen being poked constantly may never be left
    alone long enough to idle, and an OLED does some of its own upkeep while idle -- so
    hosting the tray may cost more panel life than the rest of Umbra saves. Nobody has
    measured it. Turn it off in Settings if you would rather not find out.


LICENCE

  MIT -- see LICENSE. ManagedShell is Apache 2.0 -- see THIRD-PARTY-NOTICES.md.
