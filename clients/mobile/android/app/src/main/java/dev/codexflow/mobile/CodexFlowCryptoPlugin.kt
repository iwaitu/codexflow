package dev.codexflow.mobile

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import android.util.Base64
import androidx.activity.result.ActivityResult
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import com.getcapacitor.JSObject
import com.getcapacitor.Plugin
import com.getcapacitor.PluginCall
import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.ActivityCallback
import com.getcapacitor.annotation.CapacitorPlugin
import org.json.JSONObject
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import java.util.concurrent.Executors
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

@CapacitorPlugin(name = "CodexFlowCrypto")
class CodexFlowCryptoPlugin : Plugin() {

    companion object {
        private const val KEYSTORE_PROVIDER = "AndroidKeyStore"
        private const val ALIAS_SESSION_P256 = "cf_mobile_session_p256"
        private const val ALIAS_BIO_P256 = "cf_mobile_bio_p256"
        private const val ALIAS_STATE_AES = "cf_mobile_state_aes"
        private const val PREFS_NAME = "cf_secure_state_v1"
        private const val PREF_MOBILE_ID = "mobile_id"
        private const val PREF_STATE_CIPHERTEXT = "encrypted_state"
        private const val GCM_TAG_BITS = 128

        private val P256_ORDER = BigInteger(
            "ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551",
            16,
        )
        private val P256_HALF_ORDER = P256_ORDER.shiftRight(1)

        private val BIOMETRIC_ALLOWED_REGEXES = listOf(
            Regex("^/api/gateway/approvals/[^/]+$"),
            Regex("^/api/gateway/chat$"),
            Regex("^/api/gateway/session/[^/]+/plan-mode$"),
            Regex("^/api/gateway/session/[^/]+/browser/close$"),
            Regex("^/api/gateway/session/[^/]+/computer/close$"),
            Regex("^/api/gateway/computer/emergency-stop$"),
        )

        private val EXCLUSIVE_BIOMETRIC_REGEXES = listOf(
            Regex("^/api/gateway/approvals/[^/]+$"),
            Regex("^/api/gateway/session/[^/]+/browser/close$"),
            Regex("^/api/gateway/session/[^/]+/computer/close$"),
            Regex("^/api/gateway/computer/emergency-stop$"),
        )
    }

    private val cryptoExecutor = Executors.newSingleThreadExecutor()

    private data class LoadedKeys(
        val mobileId: String,
        val sessionSpkiB64: String,
        val sessionSpkiSha256: String,
        val sessionKeyId: String,
        val bioSpkiB64: String?,
        val bioSpkiSha256: String?,
        val bioKeyId: String?,
    )

    @PluginMethod
    fun getCapabilities(call: PluginCall) {
        cryptoExecutor.execute {
            try {
                val hardwareBacked = checkOrProbeHardwareBacked()
                val bioAvailable = isStrongBiometricAvailable()
                val hasCamera = context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY)

                val out = JSObject()
                out.put("platform", "android")
                out.put("hardwareBacked", hardwareBacked)
                out.put("biometricAvailable", bioAvailable)
                out.put("qrScannerAvailable", hasCamera)
                call.resolve(out)
            } catch (e: Exception) {
                call.reject("get_capabilities_failed: ${e.message}", "get_capabilities_failed", e)
            }
        }
    }

    @PluginMethod
    fun ensureDeviceKeys(call: PluginCall) {
        val requireBio = call.getBoolean("requireBiometricKey", false) ?: false
        cryptoExecutor.execute {
            try {
                val loaded = loadOrCreateKeys(requireBio)
                val out = JSObject()
                out.put("mobileId", loaded.mobileId)
                out.put("sessionSpkiB64", loaded.sessionSpkiB64)
                out.put("sessionKeyId", loaded.sessionKeyId)
                out.put("bioSpkiB64", loaded.bioSpkiB64 ?: JSONObject.NULL)
                out.put("bioKeyId", loaded.bioKeyId ?: JSONObject.NULL)
                call.resolve(out)
            } catch (e: Exception) {
                call.reject("ensure_device_keys_failed: ${e.message}", "ensure_device_keys_failed", e)
            }
        }
    }

    @PluginMethod
    fun signPairingBundle(call: PluginCall) {
        val ticketId = call.getString("ticketId")
        val deviceId = call.getString("deviceId")
        val bindingVersion = call.getWholeNumber("bindingVersion")
        val desktopKeyId = call.getString("desktopKeyId")
        val desktopSpkiSha256 = call.getString("desktopSpkiSha256")
        val pairingNonceHex32 = call.getString("pairingNonceHex32")
        val expiresAtUnix = call.getWholeNumber("expiresAtUnix")
        val pairingSecretB64U = call.getString("pairingSecretB64U")

        if (ticketId == null || deviceId == null || bindingVersion == null ||
            desktopKeyId == null || desktopSpkiSha256 == null ||
            pairingNonceHex32 == null || expiresAtUnix == null || pairingSecretB64U == null
        ) {
            call.reject("Missing required pairing bundle parameters.", "invalid_arguments")
            return
        }

        cryptoExecutor.execute {
            try {
                val loaded = loadOrCreateKeys(requireBiometricKey = true)
                val pairRecordText = listOf(
                    "CF-PAIR-V1",
                    requireSingleLineNonEmpty(ticketId, "ticketId"),
                    requireSingleLineNonEmpty(deviceId, "deviceId"),
                    bindingVersion.toString(),
                    requireSingleLineNonEmpty(loaded.mobileId, "mobileId"),
                    requireSingleLine(desktopKeyId, "desktopKeyId"),
                    requireLowerHex(desktopSpkiSha256, 64, "desktopSpkiSha256"),
                    requireSingleLine(loaded.sessionKeyId, "sessionKeyId"),
                    requireLowerHex(loaded.sessionSpkiSha256, 64, "sessionSpkiSha256"),
                    requireSingleLine(loaded.bioKeyId ?: "", "bioKeyId"),
                    if (loaded.bioSpkiSha256.isNullOrEmpty()) "" else requireLowerHex(loaded.bioSpkiSha256, 64, "bioSpkiSha256"),
                    requireLowerHex(pairingNonceHex32, 32, "pairingNonceHex32"),
                    expiresAtUnix.toString(),
                ).joinToString("\n")

                val pairRecordBytes = pairRecordText.toByteArray(Charsets.UTF_8)
                val pairRecordSigB64U = signWithSessionKey(pairRecordBytes)

                val secretBytes = base64UrlDecode(pairingSecretB64U)
                    ?: throw IllegalArgumentException("Invalid pairingSecretB64U.")
                if (secretBytes.size != 32) {
                    throw IllegalArgumentException("pairingSecretB64U must decode to 32 bytes.")
                }

                val mac = Mac.getInstance("HmacSHA256")
                mac.init(SecretKeySpec(secretBytes, "HmacSHA256"))
                val hmacHex = toHexLower(mac.doFinal(pairRecordBytes))

                val sasDigest = MessageDigest.getInstance("SHA-256")
                    .digest("CF-SAS-V1\n$pairRecordText".toByteArray(Charsets.UTF_8))
                val sas6 = sas6FromDigest(sasDigest)

                if (loaded.bioKeyId != null) {
                    val bioPopText = listOf(
                        "CF-BIO-POP-V1",
                        requireSingleLineNonEmpty(loaded.mobileId, "mobileId"),
                        requireSingleLineNonEmpty(ticketId, "ticketId"),
                        requireSingleLineNonEmpty(loaded.bioKeyId, "bioKeyId"),
                        requireLowerHex(pairingNonceHex32, 32, "pairingNonceHex32"),
                        expiresAtUnix.toString(),
                    ).joinToString("\n")

                    signWithBioKey(
                        payload = bioPopText.toByteArray(Charsets.UTF_8),
                        promptReason = "启用生物识别进行 CodexFlow 高危审批签名",
                        onSuccess = { bioPopSigB64U ->
                            val out = JSObject()
                            out.put("mobileId", loaded.mobileId)
                            out.put("sessionSpkiB64", loaded.sessionSpkiB64)
                            out.put("bioSpkiB64", loaded.bioSpkiB64)
                            out.put("pairRecordV1Text", pairRecordText)
                            out.put("pairRecordSigB64U", pairRecordSigB64U)
                            out.put("bioPopV1Text", bioPopText)
                            out.put("bioPopSigB64U", bioPopSigB64U)
                            out.put("hmacHex", hmacHex)
                            out.put("sas6", sas6)
                            call.resolve(out)
                        },
                        onFailure = { code, msg ->
                            call.reject(msg, code)
                        },
                    )
                } else {
                    val out = JSObject()
                    out.put("mobileId", loaded.mobileId)
                    out.put("sessionSpkiB64", loaded.sessionSpkiB64)
                    out.put("bioSpkiB64", JSONObject.NULL)
                    out.put("pairRecordV1Text", pairRecordText)
                    out.put("pairRecordSigB64U", pairRecordSigB64U)
                    out.put("bioPopV1Text", JSONObject.NULL)
                    out.put("bioPopSigB64U", JSONObject.NULL)
                    out.put("hmacHex", hmacHex)
                    out.put("sas6", sas6)
                    call.resolve(out)
                }
            } catch (e: Exception) {
                call.reject("sign_pairing_bundle_failed: ${e.message}", "sign_pairing_bundle_failed", e)
            }
        }
    }

    @PluginMethod
    fun signExchange(call: PluginCall) {
        val pairingId = call.getString("pairingId")
        val nonceHex32 = call.getString("nonceHex32")
        val unixTimestamp = call.getWholeNumber("unixTimestamp")
        if (pairingId == null || nonceHex32 == null || unixTimestamp == null) {
            call.reject("Missing required signExchange parameters.", "invalid_arguments")
            return
        }

        cryptoExecutor.execute {
            try {
                val mobileId = prefs().getString(PREF_MOBILE_ID, null)
                    ?: throw IllegalStateException("Device keys not initialized.")
                val payload = listOf(
                    "CF-EXCHANGE-V1",
                    requireSingleLineNonEmpty(pairingId, "pairingId"),
                    requireSingleLineNonEmpty(mobileId, "mobileId"),
                    requireLowerHex(nonceHex32, 32, "nonceHex32"),
                    unixTimestamp.toString(),
                ).joinToString("\n")
                val signatureB64U = signWithSessionKey(payload.toByteArray(Charsets.UTF_8))
                val out = JSObject()
                out.put("signatureB64U", signatureB64U)
                call.resolve(out)
            } catch (e: Exception) {
                call.reject("sign_exchange_failed: ${e.message}", "sign_exchange_failed", e)
            }
        }
    }

    @PluginMethod
    fun signRelayRequest(call: PluginCall) {
        val deviceId = call.getString("deviceId")
        val bindingVersion = call.getWholeNumber("bindingVersion")
        val sessionEpochHex32 = call.getString("sessionEpochHex32")
        val mobileId = call.getString("mobileId")
        val keyPurpose = call.getString("keyPurpose")
        val methodUpper = call.getString("methodUpper")
        val canonicalPath = call.getString("canonicalPath")
        val canonicalQuery = call.getString("canonicalQuery", "") ?: ""
        val ifNoneMatchOrEmpty = call.getString("ifNoneMatchOrEmpty", "") ?: ""
        val uploadIdOrEmpty = call.getString("uploadIdOrEmpty", "") ?: ""
        val fileSha256OrEmpty = call.getString("fileSha256OrEmpty", "") ?: ""
        val clientOpIdOrEmpty = call.getString("clientOpIdOrEmpty", "") ?: ""
        val canonicalContentType = call.getString("canonicalContentType", "") ?: ""
        val declaredContentLength = call.getWholeNumber("declaredContentLength")
        val bodySha256LowerHex = call.getString("bodySha256LowerHex")
        val unixTimestampSeconds = call.getWholeNumber("unixTimestampSeconds")
        val nonceHex32 = call.getString("nonceHex32")
        val promptReason = call.getString("promptReason", "确认执行 CodexFlow 远程高危操作")
            ?: "确认执行 CodexFlow 远程高危操作"

        if (deviceId == null || bindingVersion == null || sessionEpochHex32 == null ||
            mobileId == null || keyPurpose == null || methodUpper == null ||
            canonicalPath == null || declaredContentLength == null ||
            bodySha256LowerHex == null || unixTimestampSeconds == null || nonceHex32 == null
        ) {
            call.reject("Missing required signRelayRequest parameters.", "invalid_arguments")
            return
        }

        cryptoExecutor.execute {
            try {
                if (keyPurpose != "device_session" && keyPurpose != "biometric_approval") {
                    throw IllegalArgumentException("Invalid keyPurpose.")
                }
                if (declaredContentLength < 0L) {
                    throw IllegalArgumentException("Negative declaredContentLength.")
                }
                if (methodUpper.uppercase() != methodUpper) {
                    throw IllegalArgumentException("methodUpper must be uppercase.")
                }
                if (canonicalPath.contains("..") || canonicalPath.contains("//")) {
                    throw IllegalArgumentException("canonicalPath contains forbidden traversal segments.")
                }
                if (canonicalQuery.startsWith("?")) {
                    throw IllegalArgumentException("canonicalQuery must not start with '?'.")
                }
                if (fileSha256OrEmpty.isNotEmpty()) {
                    requireLowerHex(fileSha256OrEmpty, 64, "fileSha256OrEmpty")
                }

                if (keyPurpose == "biometric_approval") {
                    val allowed = methodUpper == "POST" && BIOMETRIC_ALLOWED_REGEXES.any { it.matches(canonicalPath) }
                    if (!allowed) {
                        call.reject("Route not permitted for biometric_approval key.", "biometric_route_forbidden")
                        return@execute
                    }
                } else if (methodUpper == "POST" && EXCLUSIVE_BIOMETRIC_REGEXES.any { it.matches(canonicalPath) }) {
                    call.reject("High-risk route requires biometric_approval key.", "biometric_key_required")
                    return@execute
                }

                val loaded = loadOrCreateKeys(requireBiometricKey = false)
                val activeKeyId = if (keyPurpose == "biometric_approval") {
                    loaded.bioKeyId ?: run {
                        call.reject("biometric_key_unavailable", "biometric_key_unavailable")
                        return@execute
                    }
                } else {
                    loaded.sessionKeyId
                }

                val payloadText = listOf(
                    "CF-SIG-V1",
                    requireSingleLineNonEmpty(deviceId, "deviceId"),
                    bindingVersion.toString(),
                    requireLowerHex(sessionEpochHex32, 32, "sessionEpochHex32"),
                    requireSingleLineNonEmpty(mobileId, "mobileId"),
                    requireLowerHex(activeKeyId, 32, "keyId"),
                    keyPurpose,
                    requireSingleLineNonEmpty(methodUpper, "methodUpper"),
                    requireSingleLineNonEmpty(canonicalPath, "canonicalPath"),
                    requireSingleLine(canonicalQuery, "canonicalQuery"),
                    requireSingleLine(ifNoneMatchOrEmpty, "ifNoneMatchOrEmpty"),
                    requireSingleLine(uploadIdOrEmpty, "uploadIdOrEmpty"),
                    fileSha256OrEmpty,
                    requireSingleLine(clientOpIdOrEmpty, "clientOpIdOrEmpty"),
                    requireSingleLine(canonicalContentType, "canonicalContentType"),
                    declaredContentLength.toString(),
                    requireLowerHex(bodySha256LowerHex, 64, "bodySha256LowerHex"),
                    unixTimestampSeconds.toString(),
                    requireLowerHex(nonceHex32, 32, "nonceHex32"),
                ).joinToString("\n")
                val payloadBytes = payloadText.toByteArray(Charsets.UTF_8)

                if (keyPurpose == "biometric_approval") {
                    signWithBioKey(
                        payload = payloadBytes,
                        promptReason = promptReason,
                        onSuccess = { sigB64U ->
                            val out = JSObject()
                            out.put("keyId", activeKeyId)
                            out.put("sigB64U", sigB64U)
                            call.resolve(out)
                        },
                        onFailure = { code, msg ->
                            call.reject(msg, code)
                        },
                    )
                } else {
                    val sigB64U = signWithSessionKey(payloadBytes)
                    val out = JSObject()
                    out.put("keyId", activeKeyId)
                    out.put("sigB64U", sigB64U)
                    call.resolve(out)
                }
            } catch (e: Exception) {
                call.reject("sign_relay_request_failed: ${e.message}", "sign_relay_request_failed", e)
            }
        }
    }

    @PluginMethod
    fun saveSecureState(call: PluginCall) {
        val stateJson = call.getString("stateJson")
        if (stateJson == null) {
            call.reject("Missing stateJson.", "invalid_arguments")
            return
        }
        cryptoExecutor.execute {
            try {
                val aesKey = loadOrCreateAesKey()
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.ENCRYPT_MODE, aesKey)
                val iv = cipher.iv
                val ct = cipher.doFinal(stateJson.toByteArray(Charsets.UTF_8))
                val blob = ByteArray(1 + iv.size + ct.size)
                blob[0] = iv.size.toByte()
                System.arraycopy(iv, 0, blob, 1, iv.size)
                System.arraycopy(ct, 0, blob, 1 + iv.size, ct.size)

                prefs().edit()
                    .putString(PREF_STATE_CIPHERTEXT, Base64.encodeToString(blob, Base64.NO_WRAP))
                    .commit()
                call.resolve()
            } catch (e: Exception) {
                call.reject("save_secure_state_failed: ${e.message}", "save_secure_state_failed", e)
            }
        }
    }

    @PluginMethod
    fun loadSecureState(call: PluginCall) {
        cryptoExecutor.execute {
            try {
                val encoded = prefs().getString(PREF_STATE_CIPHERTEXT, null)
                val out = JSObject()
                if (encoded.isNullOrEmpty()) {
                    out.put("stateJson", JSONObject.NULL)
                    call.resolve(out)
                    return@execute
                }
                val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
                val aesKey = ks.getKey(ALIAS_STATE_AES, null) as? SecretKey
                if (aesKey == null) {
                    out.put("stateJson", JSONObject.NULL)
                    call.resolve(out)
                    return@execute
                }

                val blob = Base64.decode(encoded, Base64.DEFAULT)
                val ivLen = blob[0].toInt() and 0xFF
                val iv = blob.copyOfRange(1, 1 + ivLen)
                val ct = blob.copyOfRange(1 + ivLen, blob.size)
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, aesKey, GCMParameterSpec(GCM_TAG_BITS, iv))
                val plain = String(cipher.doFinal(ct), Charsets.UTF_8)
                out.put("stateJson", plain)
                call.resolve(out)
            } catch (_: Exception) {
                val out = JSObject()
                out.put("stateJson", JSONObject.NULL)
                call.resolve(out)
            }
        }
    }

    @PluginMethod
    fun clearAllSecureState(call: PluginCall) {
        cryptoExecutor.execute {
            try {
                val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
                for (alias in listOf(ALIAS_SESSION_P256, ALIAS_BIO_P256, ALIAS_STATE_AES)) {
                    if (ks.containsAlias(alias)) {
                        ks.deleteEntry(alias)
                    }
                }
                prefs().edit().clear().commit()
                call.resolve()
            } catch (e: Exception) {
                call.reject("clear_secure_state_failed: ${e.message}", "clear_secure_state_failed", e)
            }
        }
    }

    @PluginMethod
    fun scanQrCode(call: PluginCall) {
        val act = activity
        if (act == null) {
            call.reject("Activity unavailable.", "ui_unavailable")
            return
        }
        val intent = Intent(act, QrScannerActivity::class.java)
        startActivityForResult(call, intent, "handleQrScanResult")
    }

    @ActivityCallback
    private fun handleQrScanResult(call: PluginCall?, result: ActivityResult) {
        if (call == null) {
            return
        }
        val rawUri = result.data?.getStringExtra(QrScannerActivity.EXTRA_RAW_URI)
        if (result.resultCode == AppCompatActivity.RESULT_OK && !rawUri.isNullOrEmpty()) {
            val out = JSObject()
            out.put("rawUri", rawUri)
            call.resolve(out)
        } else {
            val errMsg = result.data?.getStringExtra(QrScannerActivity.EXTRA_ERROR)
                ?: "用户已取消扫码配对。"
            call.reject(errMsg, "qr_scan_cancelled_or_failed")
        }
    }

    // MARK: - AndroidKeyStore + StrongBox / TEE + BiometricPrompt Helpers

    private fun prefs() = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private fun isStrongBiometricAvailable(): Boolean {
        val manager = BiometricManager.from(context)
        return manager.canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) == BiometricManager.BIOMETRIC_SUCCESS
    }

    private fun checkOrProbeHardwareBacked(): Boolean {
        val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
        if (!ks.containsAlias(ALIAS_SESSION_P256)) {
            generateEcKey(ALIAS_SESSION_P256, requireBio = false)
        }
        val priv = ks.getKey(ALIAS_SESSION_P256, null) as? PrivateKey ?: return false
        return isPrivateKeyInsideSecureHardware(priv)
    }

    private fun isPrivateKeyInsideSecureHardware(priv: PrivateKey): Boolean {
        return try {
            val factory = KeyFactory.getInstance(priv.algorithm, KEYSTORE_PROVIDER)
            val keyInfo = factory.getKeySpec(priv, KeyInfo::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                keyInfo.securityLevel == KeyProperties.SECURITY_LEVEL_STRONGBOX ||
                    keyInfo.securityLevel == KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT
            } else {
                @Suppress("DEPRECATION")
                keyInfo.isInsideSecureHardware
            }
        } catch (_: Exception) {
            false
        }
    }

    private fun loadOrCreateKeys(requireBiometricKey: Boolean): LoadedKeys {
        val existingMobileId = prefs().getString(PREF_MOBILE_ID, null)
        val mobileId = if (!existingMobileId.isNullOrEmpty()) {
            existingMobileId
        } else {
            val fresh = UUID.randomUUID().toString().lowercase()
            prefs().edit().putString(PREF_MOBILE_ID, fresh).commit()
            fresh
        }

        val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
        if (!ks.containsAlias(ALIAS_SESSION_P256)) {
            generateEcKey(ALIAS_SESSION_P256, requireBio = false)
        }
        val sessionCert = ks.getCertificate(ALIAS_SESSION_P256)
            ?: throw IllegalStateException("Session EC public certificate missing.")
        val sessionSpki = sessionCert.publicKey.encoded
        val sessionHash = sha256HexLower(sessionSpki)
        val sessionKeyId = sessionHash.substring(0, 32)

        if (requireBiometricKey && !ks.containsAlias(ALIAS_BIO_P256) && isStrongBiometricAvailable()) {
            generateEcKey(ALIAS_BIO_P256, requireBio = true)
        }

        val bioCert = if (ks.containsAlias(ALIAS_BIO_P256)) ks.getCertificate(ALIAS_BIO_P256) else null
        val bioSpki = bioCert?.publicKey?.encoded
        val bioHash = bioSpki?.let { sha256HexLower(it) }

        return LoadedKeys(
            mobileId = mobileId,
            sessionSpkiB64 = Base64.encodeToString(sessionSpki, Base64.NO_WRAP),
            sessionSpkiSha256 = sessionHash,
            sessionKeyId = sessionKeyId,
            bioSpkiB64 = bioSpki?.let { Base64.encodeToString(it, Base64.NO_WRAP) },
            bioSpkiSha256 = bioHash,
            bioKeyId = bioHash?.substring(0, 32),
        )
    }

    private fun generateEcKey(alias: String, requireBio: Boolean) {
        val tryStrongBoxFirst = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
        if (tryStrongBoxFirst) {
            try {
                generateEcKeyWithBacking(alias, requireBio, useStrongBox = true)
                return
            } catch (_: StrongBoxUnavailableException) {
                // Fall back to TEE when StrongBox hardware module is unavailable
            } catch (e: Exception) {
                if (e.cause is StrongBoxUnavailableException ||
                    e.javaClass.simpleName.contains("StrongBox", ignoreCase = true) ||
                    (e.message?.contains("StrongBox", ignoreCase = true) == true)
                ) {
                    // Fall back to TEE
                } else {
                    throw e
                }
            }
        }
        generateEcKeyWithBacking(alias, requireBio, useStrongBox = false)
    }

    private fun generateEcKeyWithBacking(alias: String, requireBio: Boolean, useStrongBox: Boolean) {
        val builder = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)

        if (useStrongBox && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            builder.setIsStrongBoxBacked(true)
        }

        if (requireBio) {
            builder.setUserAuthenticationRequired(true)
            builder.setInvalidatedByBiometricEnrollment(true)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                builder.setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
            } else {
                @Suppress("DEPRECATION")
                builder.setUserAuthenticationValidityDurationSeconds(-1)
            }
        }

        val kpg = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KEYSTORE_PROVIDER)
        kpg.initialize(builder.build())
        kpg.generateKeyPair()
    }

    private fun loadOrCreateAesKey(): SecretKey {
        val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
        (ks.getKey(ALIAS_STATE_AES, null) as? SecretKey)?.let { return it }

        val kg = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE_PROVIDER)
        val spec = KeyGenParameterSpec.Builder(
            ALIAS_STATE_AES,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .build()
        kg.init(spec)
        return kg.generateKey()
    }

    private fun signWithSessionKey(payload: ByteArray): String {
        val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
        val priv = ks.getKey(ALIAS_SESSION_P256, null) as? PrivateKey
            ?: throw IllegalStateException("Session EC key missing.")
        val sig = Signature.getInstance("SHA256withECDSA")
        sig.initSign(priv)
        sig.update(payload)
        return derToP1363LowSB64U(sig.sign())
    }

    private fun signWithBioKey(
        payload: ByteArray,
        promptReason: String,
        onSuccess: (String) -> Unit,
        onFailure: (String, String) -> Unit,
    ) {
        val hostActivity = activity as? AppCompatActivity ?: run {
            onFailure("ui_unavailable", "宿主 Activity 不支持 BiometricPrompt。")
            return
        }

        val sig: Signature
        try {
            val ks = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }
            val priv = ks.getKey(ALIAS_BIO_P256, null) as? PrivateKey ?: run {
                onFailure("biometric_key_unavailable", "生物识别密钥尚未创建，请重新配对。")
                return
            }
            sig = Signature.getInstance("SHA256withECDSA")
            sig.initSign(priv)
        } catch (e: Exception) {
            onFailure("biometric_key_unavailable", "生物识别密钥已失效或被系统吊销：${e.message}")
            return
        }

        hostActivity.runOnUiThread {
            val executor = ContextCompat.getMainExecutor(hostActivity)
            val prompt = BiometricPrompt(
                hostActivity,
                executor,
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        val unlockedSig = result.cryptoObject?.signature ?: sig
                        cryptoExecutor.execute {
                            try {
                                unlockedSig.update(payload)
                                val b64u = derToP1363LowSB64U(unlockedSig.sign())
                                onSuccess(b64u)
                            } catch (e: Exception) {
                                onFailure("biometric_sign_failed", "生物签名执行失败：${e.message}")
                            }
                        }
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                        onFailure("biometric_cancelled", "生物识别验证已取消或失败：$errString")
                    }

                    override fun onAuthenticationFailed() {
                        // System prompt keeps retry UI open until cancelled or locked out
                    }
                },
            )

            val promptInfo = BiometricPrompt.PromptInfo.Builder()
                .setTitle("CodexFlow 高危安全签名确认")
                .setSubtitle(promptReason)
                .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
                .setNegativeButtonText("取消")
                .build()

            prompt.authenticate(promptInfo, BiometricPrompt.CryptoObject(sig))
        }
    }

    // MARK: - ASN.1 DER -> IEEE P1363 Low-S + Protocol Utilities

    private fun derToP1363LowSB64U(der: ByteArray): String {
        var idx = 0
        if (der.size < 8 || der[idx++] != 0x30.toByte()) {
            throw IllegalArgumentException("Invalid ECDSA DER signature header.")
        }
        val seqLenByte = der[idx++].toInt() and 0xFF
        if ((seqLenByte and 0x80) != 0) {
            idx += (seqLenByte and 0x7F)
        }
        if (der[idx++] != 0x02.toByte()) {
            throw IllegalArgumentException("Invalid ECDSA DER R marker.")
        }
        val rLen = der[idx++].toInt() and 0xFF
        val rBytes = der.copyOfRange(idx, idx + rLen)
        idx += rLen

        if (der[idx++] != 0x02.toByte()) {
            throw IllegalArgumentException("Invalid ECDSA DER S marker.")
        }
        val sLen = der[idx++].toInt() and 0xFF
        val sBytes = der.copyOfRange(idx, idx + sLen)

        val rInt = BigInteger(1, rBytes)
        var sInt = BigInteger(1, sBytes)
        if (sInt > P256_HALF_ORDER) {
            sInt = P256_ORDER.subtract(sInt)
        }

        val out = ByteArray(64)
        writeUnsignedFixed32(rInt, out, 0)
        writeUnsignedFixed32(sInt, out, 32)
        return Base64.encodeToString(out, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
    }

    private fun writeUnsignedFixed32(value: BigInteger, dest: ByteArray, offset: Int) {
        val raw = value.toByteArray()
        val srcPos = if (raw.size > 32 && raw[0] == 0.toByte()) raw.size - 32 else 0
        val copyLen = minOf(32, raw.size - srcPos)
        System.arraycopy(raw, srcPos, dest, offset + (32 - copyLen), copyLen)
    }

    private fun PluginCall.getWholeNumber(name: String): Long? {
        val raw = data?.opt(name) ?: return null
        return when (raw) {
            is Int -> raw.toLong()
            is Long -> raw
            is Number -> {
                val d = raw.toDouble()
                if (d.isFinite() && d % 1.0 == 0.0) d.toLong() else null
            }
            else -> null
        }
    }

    private fun sas6FromDigest(digest: ByteArray): String {
        val u32 = ((digest[0].toLong() and 0xFFL) shl 24) or
            ((digest[1].toLong() and 0xFFL) shl 16) or
            ((digest[2].toLong() and 0xFFL) shl 8) or
            (digest[3].toLong() and 0xFFL)
        return (u32 % 1_000_000L).toString().padStart(6, '0')
    }

    private fun sha256HexLower(bytes: ByteArray): String {
        return toHexLower(MessageDigest.getInstance("SHA-256").digest(bytes))
    }

    private fun toHexLower(bytes: ByteArray): String {
        val sb = StringBuilder(bytes.size * 2)
        for (b in bytes) {
            sb.append(String.format("%02x", b.toInt() and 0xFF))
        }
        return sb.toString()
    }

    private fun base64UrlDecode(raw: String): ByteArray? {
        return try {
            Base64.decode(raw, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
        } catch (_: Exception) {
            null
        }
    }

    private fun requireSingleLine(value: String, field: String): String {
        if (value.contains('\n') || value.contains('\r') || value.contains('\u0000')) {
            throw IllegalArgumentException("Field '$field' contains forbidden control chars.")
        }
        return value
    }

    private fun requireSingleLineNonEmpty(value: String, field: String): String {
        val clean = requireSingleLine(value, field)
        if (clean.isEmpty()) {
            throw IllegalArgumentException("Field '$field' must not be empty.")
        }
        return clean
    }

    private fun requireLowerHex(value: String, expectedLen: Int, field: String): String {
        if (value.length != expectedLen || !value.all { (it in '0'..'9') || (it in 'a'..'f') }) {
            throw IllegalArgumentException("Field '$field' must be $expectedLen lowercase hex chars.")
        }
        return value
    }
}
