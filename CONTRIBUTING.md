# Contributing to Underlit

Thanks for your interest! Bug reports, feature ideas, and pull requests are all welcome.

## Reporting bugs

Please [open an issue](../../issues/new/choose) using the **Bug report** template. Include your Windows version, Underlit version, and a snippet from `%LOCALAPPDATA%\Underlit\underlit.log` if anything looked suspicious in there.

## Suggesting features

Use the **Feature request** template. If you're not sure whether an idea fits, open a [Discussion](../../discussions) first and we can talk about it.

## Building locally

You need:
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (For installers) [Inno Setup 6](https://jrsoftware.org/isdl.php)

```powershell
dotnet publish src/Underlit/Underlit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
# The portable single-file exe is now at: publish\Underlit.exe
```

For the installer:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Underlit.iss
# Output: installer\Output\UnderlitSetup-<version>.exe
```

CI runs both steps automatically when a `v*` tag is pushed — so contributors don't need Inno Setup installed.

## Code layout

See the **Architecture notes** section in the [README](README.md).

## Pull requests

Feel free to open a PR. Small, focused changes land fastest. For anything big, opening an issue first to agree on direction saves both of us time.
