TCFBuild Release Quick Start
============================

Choose the ZIP for your computer:

  Windows: the file ending in -windows.zip
  macOS:   the file ending in -macos.zip

Windows
-------
1. Extract the entire Windows ZIP. Do not run it from inside the ZIP.
2. Open the extracted TCFBuild Windows folder.
3. Double-click setup-tcfbuild.bat.
4. Open these addresses if they do not open automatically:

     Display:           http://127.0.0.1:8080
     Control dashboard: http://127.0.0.1:8081

Keep the Command Prompt window open while using TCFBuild. Press Ctrl+C in that
window to stop it.

macOS
-----
1. Extract the entire macOS ZIP.
2. Open Terminal and change to the extracted TCFBuild macOS folder.
3. Run:

     sh ./setup-tcfbuild.command

4. Open these addresses if they do not open automatically:

     Display:           http://127.0.0.1:8080
     Control dashboard: http://127.0.0.1:8081

Keep Terminal open while using TCFBuild. Press Control+C to stop it.

If macOS blocks the embedded runtime because the downloaded ZIP is
quarantined, run this once from the extracted folder, then start TCFBuild
again:

  xattr -dr com.apple.quarantine .
  sh ./setup-tcfbuild.command

Custom Ports
------------
The default display and control ports are 8080 and 8081. To change them:

  Windows:
    setup-tcfbuild.bat --display-port=8090 --control-port=8091

  macOS:
    sh ./setup-tcfbuild.command --display-port=8090 --control-port=8091

No separate Node.js installation or administrator access is required. Each
ZIP includes the correct runtime for supported Intel, AMD, and ARM computers.
More detailed platform instructions are included inside each ZIP.
