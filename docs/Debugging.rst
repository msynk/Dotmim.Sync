Debugging DMS
====================

DMS publishes its symbols and source link metadata, so you can step into the framework code from your application without checking out the repository.

Two pieces are needed:

* The ``.pdb`` files (symbols), which map runtime addresses back to source files and line numbers.
* Source Link, which lets the debugger fetch the matching source file directly from GitHub.

Once both are configured, hitting **F11** on a call into DMS opens the source code at the right line:

.. image:: assets/StepInto.png

.. image:: assets/DebuggingDMSSourceCode.png


Symbol packages
^^^^^^^^^^^^^^^^^^^^

DMS publishes symbol packages alongside every NuGet release. Visual Studio can fetch them from the NuGet symbol server.

Open **Tools** > **Options** > **Debugging** > **Symbols**:

* Check **NuGet.org Symbol Server**.
* Optionally check **Microsoft Symbol Servers** if you also want to step into the .NET runtime itself.

If the NuGet entry isn't listed, add it manually: ``https://symbols.nuget.org/download/symbols``.

.. image:: assets/SymbolsOptions.png


Source Link
^^^^^^^^^^^^^^^^^

Source Link embeds, in the ``.pdb``, the metadata required to fetch the original source file from the source control system that produced it.

Source Link is enabled by default in modern Visual Studio versions, but two options matter:

Open **Tools** > **Options** > **Debugging** > **General**:

* Uncheck **Enable Just My Code**.
* Check **Enable Source Link support**.

.. image:: assets/DebuggingOptions.png


You can now step into DMS code as if it were yours.

.. note:: Visual Studio will prompt for confirmation the first time it downloads a source file, since the file comes from a remote URL. Accept once and the rest of the session works seamlessly.

More background:

* `Source Link <https://github.com/dotnet/sourcelink/blob/master/README.md>`_
* `Specify symbol (.pdb) and source files in the Visual Studio debugger <https://docs.microsoft.com/en-us/visualstudio/debugger/specify-symbol-dot-pdb-and-source-files-in-the-visual-studio-debugger>`_
