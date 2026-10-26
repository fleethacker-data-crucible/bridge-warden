# Encrypts/decrypts the API key with Windows DPAPI (CurrentUser scope): only the
# same Windows user on this machine can decrypt it.
#
# Dot-source it for the functions (native engine), or run it as a filter (Deno engine):
#   powershell -File secrets.ps1 -Action protect     stdin: base64(utf8 key)  -> stdout: base64(cipher)
#   powershell -File secrets.ps1 -Action unprotect   stdin: base64(cipher)    -> stdout: base64(utf8 key)
# Everything travels base64 over stdin/stdout so the key never shows up in a command line.

param([ValidateSet('protect', 'unprotect', '')][string]$Action = '')

Add-Type -AssemblyName System.Security
$SecretsEntropy = [System.Text.Encoding]::UTF8.GetBytes('CDAO_Maximus_APIs')

function Protect-Secret([string]$plain) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
    $cipher = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $SecretsEntropy, 'CurrentUser')
    return [Convert]::ToBase64String($cipher)
}

function Unprotect-Secret([string]$cipherB64) {
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($cipherB64), $SecretsEntropy, 'CurrentUser')
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

if ($Action) {
    $in = [Console]::In.ReadToEnd().Trim()
    if ($Action -eq 'protect') {
        [Console]::Out.Write((Protect-Secret ([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($in)))))
    } else {
        [Console]::Out.Write([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes((Unprotect-Secret $in))))
    }
}
