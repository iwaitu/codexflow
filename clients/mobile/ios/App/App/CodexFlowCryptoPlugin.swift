import AVFoundation
import Capacitor
import CryptoKit
import Foundation
import LocalAuthentication
import Security
import UIKit

@objc(CodexFlowCryptoPlugin)
public final class CodexFlowCryptoPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "CodexFlowCryptoPlugin"
    public let jsName = "CodexFlowCrypto"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "getCapabilities", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "ensureDeviceKeys", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "signPairingBundle", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "signExchange", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "signRelayRequest", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "saveSecureState", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "loadSecureState", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "clearAllSecureState", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "scanQrCode", returnType: CAPPluginReturnPromise),
    ]

    private static let keychainService = "dev.codexflow.mobile.crypto.v1"
    private static let accountMobileId = "cf_mobile_id"
    private static let accountSessionKeyBlob = "cf_session_p256_blob"
    private static let accountSessionIsSE = "cf_session_is_se"
    private static let accountBioKeyBlob = "cf_bio_p256_blob"
    private static let accountBioIsSE = "cf_bio_is_se"
    private static let accountSecureState = "cf_secure_state_json"

    // 26-byte ASN.1 DER SubjectPublicKeyInfo header for EC P-256 uncompressed points (65 bytes)
    private static let p256SpkiHeader = Data([
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86,
        0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A,
        0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03,
        0x42, 0x00,
    ])

    // NIST P-256 curve order n and floor((n - 1) / 2)
    private static let p256Order: [UInt8] = [
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xBC, 0xE6, 0xFA, 0xAD, 0xA7, 0x17, 0x9E, 0x84,
        0xF3, 0xB9, 0xCA, 0xC2, 0xFC, 0x63, 0x25, 0x51,
    ]
    private static let p256HalfOrder: [UInt8] = [
        0x7F, 0xFF, 0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00,
        0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xDE, 0x73, 0x7D, 0x56, 0xD3, 0x8B, 0xCF, 0x42,
        0x79, 0xDC, 0xE5, 0x61, 0x7E, 0x31, 0x92, 0xA8,
    ]

    private static let biometricAllowedPatterns = [
        "^/api/gateway/approvals/[^/]+$",
        "^/api/gateway/chat$",
        "^/api/gateway/session/[^/]+/plan-mode$",
        "^/api/gateway/session/[^/]+/browser/close$",
        "^/api/gateway/session/[^/]+/computer/close$",
        "^/api/gateway/computer/emergency-stop$",
    ]

    private static let exclusiveBiometricPatterns = [
        "^/api/gateway/approvals/[^/]+$",
        "^/api/gateway/session/[^/]+/browser/close$",
        "^/api/gateway/session/[^/]+/computer/close$",
        "^/api/gateway/computer/emergency-stop$",
    ]

    private let signQueue = DispatchQueue(label: "dev.codexflow.mobile.crypto.sign")

    @objc func getCapabilities(_ call: CAPPluginCall) {
        let context = LAContext()
        var authError: NSError?
        let bioAvailable = context.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &authError)
        let cameraAvailable = AVCaptureDevice.default(for: .video) != nil
        call.resolve([
            "platform": "ios",
            "hardwareBacked": SecureEnclave.isAvailable,
            "biometricAvailable": bioAvailable,
            "qrScannerAvailable": cameraAvailable,
        ])
    }

    @objc func ensureDeviceKeys(_ call: CAPPluginCall) {
        let requireBio = call.getBool("requireBiometricKey") ?? false
        signQueue.async {
            do {
                let summary = try self.loadOrCreateKeys(requireBiometricKey: requireBio)
                call.resolve(summary.toDictionary())
            } catch {
                call.reject("ensure_device_keys_failed: \(error.localizedDescription)", "ensure_device_keys_failed", error)
            }
        }
    }

    @objc func signPairingBundle(_ call: CAPPluginCall) {
        guard
            let ticketId = call.getString("ticketId"),
            let deviceId = call.getString("deviceId"),
            let bindingVersion = call.getInt("bindingVersion"),
            let desktopKeyId = call.getString("desktopKeyId"),
            let desktopSpkiSha256 = call.getString("desktopSpkiSha256"),
            let pairingNonceHex32 = call.getString("pairingNonceHex32"),
            let expiresAtUnix = call.getInt("expiresAtUnix"),
            let pairingSecretB64U = call.getString("pairingSecretB64U")
        else {
            call.reject("Missing required pairing bundle parameters.", "invalid_arguments")
            return
        }

        signQueue.async {
            do {
                let summary = try self.loadOrCreateKeys(requireBiometricKey: true)

                let pairRecordText = try self.buildPairV1(
                    ticketId: ticketId,
                    deviceId: deviceId,
                    bindingVersion: bindingVersion,
                    mobileId: summary.mobileId,
                    desktopKeyId: desktopKeyId,
                    desktopSpkiSha256: desktopSpkiSha256,
                    sessionKeyId: summary.sessionKeyId,
                    sessionSpkiSha256: summary.sessionSpkiSha256,
                    bioKeyId: summary.bioKeyId,
                    bioSpkiSha256: summary.bioSpkiSha256,
                    pairingNonceHex32: pairingNonceHex32,
                    expiresAtUnix: expiresAtUnix
                )
                let pairRecordData = Data(pairRecordText.utf8)
                let pairRecordSigB64U = try self.signWithSessionKey(data: pairRecordData)

                var bioPopText: Any = NSNull()
                var bioPopSigB64U: Any = NSNull()
                if let bioKeyId = summary.bioKeyId {
                    let text = try self.buildBioPopV1(
                        mobileId: summary.mobileId,
                        ticketId: ticketId,
                        bioKeyId: bioKeyId,
                        pairingNonceHex32: pairingNonceHex32,
                        expiresAtUnix: expiresAtUnix
                    )
                    let sig = try self.signWithBioKey(
                        data: Data(text.utf8),
                        promptReason: "启用 Face ID / Touch ID 进行高危审批签名认证"
                    )
                    bioPopText = text
                    bioPopSigB64U = sig
                }

                guard let secretData = Self.base64UrlDecode(pairingSecretB64U), secretData.count == 32 else {
                    call.reject("pairingSecretB64U must decode to 32 bytes.", "invalid_pairing_secret")
                    return
                }

                let symKey = SymmetricKey(data: secretData)
                let hmac = HMAC<SHA256>.authenticationCode(for: pairRecordData, using: symKey)
                let hmacHex = Self.toHexLower(Data(hmac))

                let sasInput = Data(("CF-SAS-V1\n" + pairRecordText).utf8)
                let sasDigest = Data(SHA256.hash(data: sasInput))
                let sas6 = Self.sas6FromDigest(sasDigest)

                call.resolve([
                    "mobileId": summary.mobileId,
                    "sessionSpkiB64": summary.sessionSpkiB64,
                    "bioSpkiB64": (summary.bioSpkiB64 as Any?) ?? NSNull(),
                    "pairRecordV1Text": pairRecordText,
                    "pairRecordSigB64U": pairRecordSigB64U,
                    "bioPopV1Text": bioPopText,
                    "bioPopSigB64U": bioPopSigB64U,
                    "hmacHex": hmacHex,
                    "sas6": sas6,
                ])
            } catch {
                call.reject("sign_pairing_bundle_failed: \(error.localizedDescription)", "sign_pairing_bundle_failed", error)
            }
        }
    }

    @objc func signExchange(_ call: CAPPluginCall) {
        guard
            let pairingId = call.getString("pairingId"),
            let nonceHex32 = call.getString("nonceHex32"),
            let unixTimestamp = call.getInt("unixTimestamp")
        else {
            call.reject("Missing required signExchange parameters.", "invalid_arguments")
            return
        }

        signQueue.async {
            do {
                guard let mobileId = self.readKeychainString(account: Self.accountMobileId) else {
                    throw NSError(domain: "CodexFlowCrypto", code: 1, userInfo: [NSLocalizedDescriptionKey: "Device keys not initialized."])
                }
                let text = [
                    "CF-EXCHANGE-V1",
                    try Self.requireSingleLineNonEmpty(pairingId, name: "pairingId"),
                    try Self.requireSingleLineNonEmpty(mobileId, name: "mobileId"),
                    try Self.requireLowerHex(nonceHex32, length: 32, name: "nonceHex32"),
                    String(unixTimestamp),
                ].joined(separator: "\n")

                let signatureB64U = try self.signWithSessionKey(data: Data(text.utf8))
                call.resolve(["signatureB64U": signatureB64U])
            } catch {
                call.reject("sign_exchange_failed: \(error.localizedDescription)", "sign_exchange_failed", error)
            }
        }
    }

    @objc func signRelayRequest(_ call: CAPPluginCall) {
        guard
            let deviceId = call.getString("deviceId"),
            let bindingVersion = call.getInt("bindingVersion"),
            let sessionEpochHex32 = call.getString("sessionEpochHex32"),
            let mobileId = call.getString("mobileId"),
            let keyPurpose = call.getString("keyPurpose"),
            let methodUpper = call.getString("methodUpper"),
            let canonicalPath = call.getString("canonicalPath"),
            let declaredContentLength = call.getInt("declaredContentLength"),
            let bodySha256LowerHex = call.getString("bodySha256LowerHex"),
            let unixTimestampSeconds = call.getInt("unixTimestampSeconds"),
            let nonceHex32 = call.getString("nonceHex32")
        else {
            call.reject("Missing required signRelayRequest parameters.", "invalid_arguments")
            return
        }

        let canonicalQuery = call.getString("canonicalQuery") ?? ""
        let ifNoneMatchOrEmpty = call.getString("ifNoneMatchOrEmpty") ?? ""
        let uploadIdOrEmpty = call.getString("uploadIdOrEmpty") ?? ""
        let fileSha256OrEmpty = call.getString("fileSha256OrEmpty") ?? ""
        let clientOpIdOrEmpty = call.getString("clientOpIdOrEmpty") ?? ""
        let canonicalContentType = call.getString("canonicalContentType") ?? ""
        let promptReason = call.getString("promptReason") ?? "确认执行 CodexFlow 远程高危操作"

        signQueue.async {
            do {
                if keyPurpose != "device_session" && keyPurpose != "biometric_approval" {
                    throw NSError(domain: "CodexFlowCrypto", code: 2, userInfo: [NSLocalizedDescriptionKey: "Invalid keyPurpose."])
                }
                if declaredContentLength < 0 {
                    throw NSError(domain: "CodexFlowCrypto", code: 3, userInfo: [NSLocalizedDescriptionKey: "Negative content length."])
                }
                if methodUpper.uppercased() != methodUpper {
                    throw NSError(domain: "CodexFlowCrypto", code: 4, userInfo: [NSLocalizedDescriptionKey: "methodUpper must be uppercase."])
                }
                if canonicalPath.contains("..") || canonicalPath.contains("//") {
                    throw NSError(domain: "CodexFlowCrypto", code: 5, userInfo: [NSLocalizedDescriptionKey: "canonicalPath contains forbidden traversal sequences."])
                }
                if canonicalQuery.hasPrefix("?") {
                    throw NSError(domain: "CodexFlowCrypto", code: 6, userInfo: [NSLocalizedDescriptionKey: "canonicalQuery must not start with '?'."])
                }
                if !fileSha256OrEmpty.isEmpty {
                    _ = try Self.requireLowerHex(fileSha256OrEmpty, length: 64, name: "fileSha256OrEmpty")
                }

                if keyPurpose == "biometric_approval" {
                    let permitted = methodUpper == "POST" && Self.matchesAnyPattern(canonicalPath, patterns: Self.biometricAllowedPatterns)
                    if !permitted {
                        call.reject("Route not permitted for biometric_approval key.", "biometric_route_forbidden")
                        return
                    }
                } else if methodUpper == "POST" && Self.matchesAnyPattern(canonicalPath, patterns: Self.exclusiveBiometricPatterns) {
                    call.reject("High-risk route must use biometric_approval key.", "biometric_key_required")
                    return
                }

                let summary = try self.loadOrCreateKeys(requireBiometricKey: false)
                let activeKeyId: String
                if keyPurpose == "biometric_approval" {
                    guard let bioId = summary.bioKeyId else {
                        call.reject("biometric_key_unavailable", "biometric_key_unavailable")
                        return
                    }
                    activeKeyId = bioId
                } else {
                    activeKeyId = summary.sessionKeyId
                }

                let lines = [
                    "CF-SIG-V1",
                    try Self.requireSingleLineNonEmpty(deviceId, name: "deviceId"),
                    String(bindingVersion),
                    try Self.requireLowerHex(sessionEpochHex32, length: 32, name: "sessionEpochHex32"),
                    try Self.requireSingleLineNonEmpty(mobileId, name: "mobileId"),
                    try Self.requireLowerHex(activeKeyId, length: 32, name: "keyId"),
                    keyPurpose,
                    try Self.requireSingleLineNonEmpty(methodUpper, name: "methodUpper"),
                    try Self.requireSingleLineNonEmpty(canonicalPath, name: "canonicalPath"),
                    try Self.requireSingleLine(canonicalQuery, name: "canonicalQuery"),
                    try Self.requireSingleLine(ifNoneMatchOrEmpty, name: "ifNoneMatchOrEmpty"),
                    try Self.requireSingleLine(uploadIdOrEmpty, name: "uploadIdOrEmpty"),
                    fileSha256OrEmpty,
                    try Self.requireSingleLine(clientOpIdOrEmpty, name: "clientOpIdOrEmpty"),
                    try Self.requireSingleLine(canonicalContentType, name: "canonicalContentType"),
                    String(declaredContentLength),
                    try Self.requireLowerHex(bodySha256LowerHex, length: 64, name: "bodySha256LowerHex"),
                    String(unixTimestampSeconds),
                    try Self.requireLowerHex(nonceHex32, length: 32, name: "nonceHex32"),
                ]
                let canonicalPayload = Data(lines.joined(separator: "\n").utf8)

                let sigB64U: String
                if keyPurpose == "biometric_approval" {
                    sigB64U = try self.signWithBioKey(data: canonicalPayload, promptReason: promptReason)
                } else {
                    sigB64U = try self.signWithSessionKey(data: canonicalPayload)
                }

                call.resolve([
                    "keyId": activeKeyId,
                    "sigB64U": sigB64U,
                ])
            } catch {
                call.reject("sign_relay_request_failed: \(error.localizedDescription)", "sign_relay_request_failed", error)
            }
        }
    }

    @objc func saveSecureState(_ call: CAPPluginCall) {
        guard let stateJson = call.getString("stateJson") else {
            call.reject("Missing stateJson.", "invalid_arguments")
            return
        }
        signQueue.async {
            do {
                try self.writeKeychainData(Data(stateJson.utf8), account: Self.accountSecureState)
                call.resolve()
            } catch {
                call.reject("save_secure_state_failed: \(error.localizedDescription)", "save_secure_state_failed", error)
            }
        }
    }

    @objc func loadSecureState(_ call: CAPPluginCall) {
        signQueue.async {
            if let data = self.readKeychainData(account: Self.accountSecureState),
               let json = String(data: data, encoding: .utf8) {
                call.resolve(["stateJson": json])
            } else {
                call.resolve(["stateJson": NSNull()])
            }
        }
    }

    @objc func clearAllSecureState(_ call: CAPPluginCall) {
        signQueue.async {
            for acct in [
                Self.accountMobileId,
                Self.accountSessionKeyBlob,
                Self.accountSessionIsSE,
                Self.accountBioKeyBlob,
                Self.accountBioIsSE,
                Self.accountSecureState,
            ] {
                self.deleteKeychainItem(account: acct)
            }
            call.resolve()
        }
    }

    @objc func scanQrCode(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let presentingVC = self.bridge?.viewController else {
                call.reject("View controller unavailable.", "ui_unavailable")
                return
            }

            let status = AVCaptureDevice.authorizationStatus(for: .video)
            let launchScanner = {
                let scanner = CodexQrScannerViewController { result in
                    switch result {
                    case .success(let code):
                        call.resolve(["rawUri": code])
                    case .failure(let err):
                        call.reject(err.localizedDescription, "qr_scan_cancelled_or_failed", err)
                    }
                }
                scanner.modalPresentationStyle = .fullScreen
                presentingVC.present(scanner, animated: true)
            }

            if status == .authorized {
                launchScanner()
            } else if status == .notDetermined {
                AVCaptureDevice.requestAccess(for: .video) { granted in
                    DispatchQueue.main.async {
                        if granted {
                            launchScanner()
                        } else {
                            call.reject("未授予相机权限，请在系统设置中允许 CodexFlow 使用相机。", "camera_permission_denied")
                        }
                    }
                }
            } else {
                call.reject("相机权限已被拒绝，请在系统设置中启用后再扫码。", "camera_permission_denied")
            }
        }
    }

    // MARK: - Keychain & Secure Enclave Internals

    private struct LoadedKeySummary {
        let mobileId: String
        let sessionSpkiB64: String
        let sessionSpkiSha256: String
        let sessionKeyId: String
        let bioSpkiB64: String?
        let bioSpkiSha256: String?
        let bioKeyId: String?

        func toDictionary() -> [String: Any] {
            return [
                "mobileId": mobileId,
                "sessionSpkiB64": sessionSpkiB64,
                "sessionKeyId": sessionKeyId,
                "bioSpkiB64": (bioSpkiB64 as Any?) ?? NSNull(),
                "bioKeyId": (bioKeyId as Any?) ?? NSNull(),
            ]
        }
    }

    private func loadOrCreateKeys(requireBiometricKey: Bool) throws -> LoadedKeySummary {
        let mobileId: String
        if let existing = readKeychainString(account: Self.accountMobileId), !existing.isEmpty {
            mobileId = existing
        } else {
            let fresh = UUID().uuidString.lowercased()
            try writeKeychainData(Data(fresh.utf8), account: Self.accountMobileId)
            mobileId = fresh
        }

        let sessionPubX963: Data
        if let existingBlob = readKeychainData(account: Self.accountSessionKeyBlob) {
            let isSE = readKeychainString(account: Self.accountSessionIsSE) == "1"
            if isSE && SecureEnclave.isAvailable {
                let priv = try SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: existingBlob)
                sessionPubX963 = priv.publicKey.x963Representation
            } else {
                let priv = try P256.Signing.PrivateKey(rawRepresentation: existingBlob)
                sessionPubX963 = priv.publicKey.x963Representation
            }
        } else if SecureEnclave.isAvailable {
            var accessErr: Unmanaged<CFError>?
            guard let ac = SecAccessControlCreateWithFlags(
                kCFAllocatorDefault,
                kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
                [.privateKeyUsage],
                &accessErr
            ) else {
                throw accessErr?.takeRetainedValue() ?? NSError(domain: "CodexFlowCrypto", code: 10)
            }
            let priv = try SecureEnclave.P256.Signing.PrivateKey(accessControl: ac)
            try writeKeychainData(priv.dataRepresentation, account: Self.accountSessionKeyBlob)
            try writeKeychainData(Data("1".utf8), account: Self.accountSessionIsSE)
            sessionPubX963 = priv.publicKey.x963Representation
        } else {
            let priv = P256.Signing.PrivateKey()
            try writeKeychainData(priv.rawRepresentation, account: Self.accountSessionKeyBlob)
            try writeKeychainData(Data("0".utf8), account: Self.accountSessionIsSE)
            sessionPubX963 = priv.publicKey.x963Representation
        }

        let sessionSpki = Self.wrapP256Spki(x963: sessionPubX963)
        let sessionHash = Self.toHexLower(Data(SHA256.hash(data: sessionSpki)))
        let sessionKeyId = String(sessionHash.prefix(32))

        var bioPubX963: Data?
        if let existingBioBlob = readKeychainData(account: Self.accountBioKeyBlob) {
            let bioSE = readKeychainString(account: Self.accountBioIsSE) == "1"
            if bioSE && SecureEnclave.isAvailable {
                let priv = try SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: existingBioBlob)
                bioPubX963 = priv.publicKey.x963Representation
            } else if !bioSE {
                let priv = try P256.Signing.PrivateKey(rawRepresentation: existingBioBlob)
                bioPubX963 = priv.publicKey.x963Representation
            }
        } else if requireBiometricKey {
            let ctx = LAContext()
            var authErr: NSError?
            let canBio = ctx.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &authErr)
            if canBio {
                if SecureEnclave.isAvailable {
                    var accessErr: Unmanaged<CFError>?
                    guard let ac = SecAccessControlCreateWithFlags(
                        kCFAllocatorDefault,
                        kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
                        [.privateKeyUsage, .biometryCurrentSet],
                        &accessErr
                    ) else {
                        throw accessErr?.takeRetainedValue() ?? NSError(domain: "CodexFlowCrypto", code: 11)
                    }
                    let priv = try SecureEnclave.P256.Signing.PrivateKey(accessControl: ac)
                    try writeKeychainData(priv.dataRepresentation, account: Self.accountBioKeyBlob)
                    try writeKeychainData(Data("1".utf8), account: Self.accountBioIsSE)
                    bioPubX963 = priv.publicKey.x963Representation
                } else {
                    // Simulator / non-SE test path: still gated by LAContext on every signature
                    let priv = P256.Signing.PrivateKey()
                    try writeKeychainData(priv.rawRepresentation, account: Self.accountBioKeyBlob)
                    try writeKeychainData(Data("0".utf8), account: Self.accountBioIsSE)
                    bioPubX963 = priv.publicKey.x963Representation
                }
            }
        }

        let bioSpki = bioPubX963.map { Self.wrapP256Spki(x963: $0) }
        let bioHash = bioSpki.map { Self.toHexLower(Data(SHA256.hash(data: $0))) }

        return LoadedKeySummary(
            mobileId: mobileId,
            sessionSpkiB64: sessionSpki.base64EncodedString(),
            sessionSpkiSha256: sessionHash,
            sessionKeyId: sessionKeyId,
            bioSpkiB64: bioSpki?.base64EncodedString(),
            bioSpkiSha256: bioHash,
            bioKeyId: bioHash.map { String($0.prefix(32)) }
        )
    }

    private func signWithSessionKey(data: Data) throws -> String {
        guard let blob = readKeychainData(account: Self.accountSessionKeyBlob) else {
            throw NSError(domain: "CodexFlowCrypto", code: 20, userInfo: [NSLocalizedDescriptionKey: "Session key missing."])
        }
        let isSE = readKeychainString(account: Self.accountSessionIsSE) == "1"
        let rawSig: Data
        if isSE && SecureEnclave.isAvailable {
            let priv = try SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: blob)
            rawSig = try priv.signature(for: data).rawRepresentation
        } else {
            let priv = try P256.Signing.PrivateKey(rawRepresentation: blob)
            rawSig = try priv.signature(for: data).rawRepresentation
        }
        return Self.base64UrlEncode(try Self.normalizeP1363LowS(rawSig))
    }

    private func signWithBioKey(data: Data, promptReason: String) throws -> String {
        guard let blob = readKeychainData(account: Self.accountBioKeyBlob) else {
            throw NSError(domain: "CodexFlowCrypto", code: 21, userInfo: [NSLocalizedDescriptionKey: "biometric_key_unavailable"])
        }
        let bioSE = readKeychainString(account: Self.accountBioIsSE) == "1"
        let context = LAContext()
        context.localizedReason = promptReason
        context.localizedFallbackTitle = ""

        if bioSE && SecureEnclave.isAvailable {
            let priv = try SecureEnclave.P256.Signing.PrivateKey(
                dataRepresentation: blob,
                authenticationContext: context
            )
            let sig = try priv.signature(for: data).rawRepresentation
            return Self.base64UrlEncode(try Self.normalizeP1363LowS(sig))
        }

        // Software fallback path (Simulator only): explicitly require biometrics before signing
        let sem = DispatchSemaphore(value: 0)
        var bioOk = false
        var bioErr: Error?
        context.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, localizedReason: promptReason) { ok, err in
            bioOk = ok
            bioErr = err
            sem.signal()
        }
        sem.wait()
        guard bioOk else {
            throw bioErr ?? NSError(domain: "CodexFlowCrypto", code: 22, userInfo: [NSLocalizedDescriptionKey: "Biometric authentication failed."])
        }
        let priv = try P256.Signing.PrivateKey(rawRepresentation: blob)
        return Self.base64UrlEncode(try Self.normalizeP1363LowS(try priv.signature(for: data).rawRepresentation))
    }

    // MARK: - Keychain CRUD (ThisDeviceOnly, No iCloud Backup)

    private func readKeychainData(account: String) -> Data? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: Self.keychainService,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else {
            return nil
        }
        return data
    }

    private func readKeychainString(account: String) -> String? {
        guard let data = readKeychainData(account: account) else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    private func writeKeychainData(_ data: Data, account: String) throws {
        deleteKeychainItem(account: account)
        let attrs: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: Self.keychainService,
            kSecAttrAccount: account,
            kSecValueData: data,
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let status = SecItemAdd(attrs as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }
    }

    private func deleteKeychainItem(account: String) {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: Self.keychainService,
            kSecAttrAccount: account,
        ]
        SecItemDelete(query as CFDictionary)
    }

    // MARK: - Canonical Protocol Builders & Low-S Crypto Math

    private func buildPairV1(
        ticketId: String,
        deviceId: String,
        bindingVersion: Int,
        mobileId: String,
        desktopKeyId: String,
        desktopSpkiSha256: String,
        sessionKeyId: String,
        sessionSpkiSha256: String,
        bioKeyId: String?,
        bioSpkiSha256: String?,
        pairingNonceHex32: String,
        expiresAtUnix: Int
    ) throws -> String {
        let bKey = try Self.requireSingleLine(bioKeyId ?? "", name: "bioKeyId")
        let bHash = (bioSpkiSha256 ?? "").isEmpty ? "" : try Self.requireLowerHex(bioSpkiSha256!, length: 64, name: "bioSpkiSha256")
        return [
            "CF-PAIR-V1",
            try Self.requireSingleLineNonEmpty(ticketId, name: "ticketId"),
            try Self.requireSingleLineNonEmpty(deviceId, name: "deviceId"),
            String(bindingVersion),
            try Self.requireSingleLineNonEmpty(mobileId, name: "mobileId"),
            try Self.requireSingleLine(desktopKeyId, name: "desktopKeyId"),
            try Self.requireLowerHex(desktopSpkiSha256, length: 64, name: "desktopSpkiSha256"),
            try Self.requireSingleLine(sessionKeyId, name: "sessionKeyId"),
            try Self.requireLowerHex(sessionSpkiSha256, length: 64, name: "sessionSpkiSha256"),
            bKey,
            bHash,
            try Self.requireLowerHex(pairingNonceHex32, length: 32, name: "pairingNonceHex32"),
            String(expiresAtUnix),
        ].joined(separator: "\n")
    }

    private func buildBioPopV1(
        mobileId: String,
        ticketId: String,
        bioKeyId: String,
        pairingNonceHex32: String,
        expiresAtUnix: Int
    ) throws -> String {
        return [
            "CF-BIO-POP-V1",
            try Self.requireSingleLineNonEmpty(mobileId, name: "mobileId"),
            try Self.requireSingleLineNonEmpty(ticketId, name: "ticketId"),
            try Self.requireSingleLineNonEmpty(bioKeyId, name: "bioKeyId"),
            try Self.requireLowerHex(pairingNonceHex32, length: 32, name: "pairingNonceHex32"),
            String(expiresAtUnix),
        ].joined(separator: "\n")
    }

    private static func wrapP256Spki(x963: Data) -> Data {
        var spki = Data(capacity: p256SpkiHeader.count + x963.count)
        spki.append(p256SpkiHeader)
        spki.append(x963)
        return spki
    }

    private static func normalizeP1363LowS(_ raw: Data) throws -> Data {
        guard raw.count == 64 else {
            throw NSError(domain: "CodexFlowCrypto", code: 30, userInfo: [NSLocalizedDescriptionKey: "Expected 64-byte P1363 ECDSA signature."])
        }
        let sBytes = Array(raw[32..<64])
        if compareBigEndian32(sBytes, p256HalfOrder) > 0 {
            let lowS = subtractBigEndian32(p256Order, sBytes)
            var out = Data(raw[0..<32])
            out.append(contentsOf: lowS)
            return out
        }
        return raw
    }

    private static func compareBigEndian32(_ a: [UInt8], _ b: [UInt8]) -> Int {
        for i in 0..<32 {
            if a[i] < b[i] { return -1 }
            if a[i] > b[i] { return 1 }
        }
        return 0
    }

    private static func subtractBigEndian32(_ a: [UInt8], _ b: [UInt8]) -> [UInt8] {
        var out = [UInt8](repeating: 0, count: 32)
        var borrow: Int = 0
        for i in stride(from: 31, through: 0, by: -1) {
            let diff = Int(a[i]) - Int(b[i]) - borrow
            if diff < 0 {
                out[i] = UInt8(diff + 256)
                borrow = 1
            } else {
                out[i] = UInt8(diff)
                borrow = 0
            }
        }
        return out
    }

    private static func sas6FromDigest(_ digest: Data) -> String {
        let u32 =
            (UInt32(digest[0]) << 24) |
            (UInt32(digest[1]) << 16) |
            (UInt32(digest[2]) << 8) |
            UInt32(digest[3])
        return String(format: "%06u", u32 % 1_000_000)
    }

    private static func matchesAnyPattern(_ path: String, patterns: [String]) -> Bool {
        let range = NSRange(path.startIndex..<path.endIndex, in: path)
        for pattern in patterns {
            if let rx = try? NSRegularExpression(pattern: pattern),
               rx.firstMatch(in: path, options: [], range: range) != nil {
                return true
            }
        }
        return false
    }

    private static func requireSingleLine(_ value: String, name: String) throws -> String {
        if value.contains("\n") || value.contains("\r") || value.contains("\0") {
            throw NSError(domain: "CodexFlowCrypto", code: 40, userInfo: [NSLocalizedDescriptionKey: "Field '\(name)' contains forbidden control characters."])
        }
        return value
    }

    private static func requireSingleLineNonEmpty(_ value: String, name: String) throws -> String {
        let clean = try requireSingleLine(value, name: name)
        guard !clean.isEmpty else {
            throw NSError(domain: "CodexFlowCrypto", code: 41, userInfo: [NSLocalizedDescriptionKey: "Field '\(name)' must not be empty."])
        }
        return clean
    }

    private static func requireLowerHex(_ value: String, length: Int, name: String) throws -> String {
        guard value.count == length, value.allSatisfy({ ($0 >= "0" && $0 <= "9") || ($0 >= "a" && $0 <= "f") }) else {
            throw NSError(domain: "CodexFlowCrypto", code: 42, userInfo: [NSLocalizedDescriptionKey: "Field '\(name)' must be \(length) lowercase hex chars."])
        }
        return value
    }

    private static func toHexLower(_ data: Data) -> String {
        return data.map { String(format: "%02x", $0) }.joined()
    }

    private static func base64UrlEncode(_ data: Data) -> String {
        return data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private static func base64UrlDecode(_ raw: String) -> Data? {
        var b64 = raw.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        let rem = b64.count % 4
        if rem == 2 { b64 += "==" }
        else if rem == 3 { b64 += "=" }
        else if rem == 1 { return nil }
        return Data(base64Encoded: b64)
    }
}

private final class CodexQrScannerViewController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    private let onComplete: (Result<String, Error>) -> Void
    private let captureSession = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "dev.codexflow.mobile.qr.session")
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private var completed = false

    init(onComplete: @escaping (Result<String, Error>) -> Void) {
        self.onComplete = onComplete
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black

        guard let videoDevice = AVCaptureDevice.default(for: .video),
              let videoInput = try? AVCaptureDeviceInput(device: videoDevice),
              captureSession.canAddInput(videoInput) else {
            finish(.failure(NSError(domain: "CodexFlowQR", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法打开后置摄像头。"])))
            return
        }
        captureSession.addInput(videoInput)

        let metadataOutput = AVCaptureMetadataOutput()
        guard captureSession.canAddOutput(metadataOutput) else {
            finish(.failure(NSError(domain: "CodexFlowQR", code: 2, userInfo: [NSLocalizedDescriptionKey: "无法初始化二维码识别器。"])))
            return
        }
        captureSession.addOutput(metadataOutput)
        metadataOutput.setMetadataObjectsDelegate(self, queue: DispatchQueue.main)
        metadataOutput.metadataObjectTypes = [.qr]

        let preview = AVCaptureVideoPreviewLayer(session: captureSession)
        preview.videoGravity = .resizeAspectFill
        preview.frame = view.layer.bounds
        view.layer.addSublayer(preview)
        self.previewLayer = preview

        setupOverlay()

        sessionQueue.async { [weak self] in
            self?.captureSession.startRunning()
        }
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.layer.bounds
    }

    private func setupOverlay() {
        let cancelBtn = UIButton(type: .system)
        cancelBtn.setTitle("取消", for: .normal)
        cancelBtn.setTitleColor(.white, for: .normal)
        cancelBtn.titleLabel?.font = .systemFont(ofSize: 16, weight: .semibold)
        cancelBtn.backgroundColor = UIColor.black.withAlphaComponent(0.6)
        cancelBtn.layer.cornerRadius = 10
        cancelBtn.contentEdgeInsets = UIEdgeInsets(top: 10, left: 18, bottom: 10, right: 18)
        cancelBtn.translatesAutoresizingMaskIntoConstraints = false
        cancelBtn.addTarget(self, action: #selector(handleCancel), for: .touchUpInside)

        let hintLabel = UILabel()
        hintLabel.text = "对准桌面端「设置 → 远程控制」配对二维码"
        hintLabel.textColor = .white
        hintLabel.font = .systemFont(ofSize: 14, weight: .medium)
        hintLabel.textAlignment = .center
        hintLabel.backgroundColor = UIColor.black.withAlphaComponent(0.55)
        hintLabel.layer.cornerRadius = 8
        hintLabel.clipsToBounds = true
        hintLabel.translatesAutoresizingMaskIntoConstraints = false

        view.addSubview(cancelBtn)
        view.addSubview(hintLabel)

        NSLayoutConstraint.activate([
            cancelBtn.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 16),
            cancelBtn.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 20),
            hintLabel.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -36),
            hintLabel.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            hintLabel.widthAnchor.constraint(lessThanOrEqualTo: view.widthAnchor, constant: -40),
        ])
    }

    @objc private func handleCancel() {
        finish(.failure(NSError(domain: "CodexFlowQR", code: 3, userInfo: [NSLocalizedDescriptionKey: "用户已取消扫码配对。"])))
    }

    func metadataOutput(
        _ output: AVCaptureMetadataOutput,
        didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        guard !completed,
              let obj = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
              let code = obj.stringValue,
              !code.isEmpty else {
            return
        }
        finish(.success(code))
    }

    private func finish(_ result: Result<String, Error>) {
        guard !completed else { return }
        completed = true
        sessionQueue.async { [weak self] in
            self?.captureSession.stopRunning()
        }
        dismiss(animated: true) { [onComplete] in
            onComplete(result)
        }
    }
}
