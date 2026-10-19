import Foundation
import CryptoKit

// Formats supported by Sparkle 2.10 common_cli/Secret.swift:
// 32-byte Ed25519 seed, or legacy 64-byte expanded private key + 32-byte public key.
func validate() -> Bool {
    let env = ProcessInfo.processInfo.environment
    guard let privateText = env["PRIVATE_KEY"], let publicText = env["PUBLIC_KEY"],
          let secret = Data(base64Encoded: privateText.trimmingCharacters(in: .whitespacesAndNewlines)),
          let expected = Data(base64Encoded: publicText.trimmingCharacters(in: .whitespacesAndNewlines)),
          expected.count == 32 else { return false }
    let actual: Data
    if secret.count == 32 {
        guard let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: secret) else { return false }
        actual = key.publicKey.rawRepresentation
    } else if secret.count == 96 {
        actual = Data(secret.suffix(32))
    } else { return false }
    return actual == expected
}
if !validate() {
    // Never include key material in errors or tool arguments.
    fputs("Invalid Sparkle key configuration: expected a Base64 32-byte seed or legacy 96-byte export matching SPARKLE_PUBLIC_KEY.\n", stderr)
    exit(1)
}
print("Sparkle update key configuration validated.")
