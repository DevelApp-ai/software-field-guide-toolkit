# Releases

Releases live at https://github.com/DevelApp-ai/software-field-guide-toolkit/releases and are signed with the DevelApp Certum cloud code signing certificate (Authenticode for the Windows binary, an author signature for the tool package, both RFC 3161-timestamped). Assets per release:

- `sfg-<version>-linux-x64.tar.gz`, `sfg-<version>-linux-arm64.tar.gz` — self-contained single-file binaries
- `sfg-<version>-win-x64.zip` — the Windows binary, Authenticode-signed
- `Sfg.Tool.<version>.nupkg` — the .NET tool package, author-signed
- `sfg-<version>-checksums.txt` — SHA-256 over every asset

Verify after download:

```bash
sha256sum --check sfg-<version>-checksums.txt
dotnet nuget verify Sfg.Tool.<version>.nupkg
```

On Windows, additionally check the extracted `sfg.exe` signature: `Get-AuthenticodeSignature sfg.exe` must report `Valid` (or `signtool verify /pa /all sfg.exe`).

Install the tool package from a release download:

```bash
dotnet tool install --global Sfg.Tool --add-source <directory containing the nupkg>
```
