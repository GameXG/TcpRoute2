package io.tcproute.android

import android.content.Context
import java.io.File
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicBoolean

object GeoDataManager {
    const val INTERVAL_OFF = 0L
    const val INTERVAL_DAILY = 24L * 60L * 60L * 1000L
    const val INTERVAL_WEEKLY = 7L * INTERVAL_DAILY

    data class Snapshot(
        val interval: Long,
        val lastSuccess: Long,
        val lastError: String?,
        val geositeBytes: Long,
        val geoipBytes: Long
    )

    private data class Download(val name: String, val file: File, val checksum: String)

    fun snapshot(context: Context): Snapshot {
        val preferences = preferences(context)
        return Snapshot(
            interval = preferences.getLong(KEY_INTERVAL, INTERVAL_OFF),
            lastSuccess = preferences.getLong(KEY_LAST_SUCCESS, 0L).takeIf { it > 0 }
                ?: listOf(File(context.filesDir, GEOSITE), File(context.filesDir, GEOIP))
                    .filter(File::isFile)
                    .minOfOrNull(File::lastModified)
                    .orZero(),
            lastError = preferences.getString(KEY_LAST_ERROR, null),
            geositeBytes = File(context.filesDir, GEOSITE).takeIf(File::isFile)?.length() ?: 0L,
            geoipBytes = File(context.filesDir, GEOIP).takeIf(File::isFile)?.length() ?: 0L
        )
    }

    fun setInterval(context: Context, interval: Long) {
        require(interval in setOf(INTERVAL_OFF, INTERVAL_DAILY, INTERVAL_WEEKLY))
        preferences(context).edit().putLong(KEY_INTERVAL, interval).apply()
    }

    fun updateIfDue(context: Context) {
        val state = snapshot(context)
        if (state.interval == INTERVAL_OFF || System.currentTimeMillis() - state.lastSuccess < state.interval) return
        update(context, null)
    }

    fun update(context: Context, callback: ((Result<Snapshot>) -> Unit)?) {
        val appContext = context.applicationContext
        if (!updating.compareAndSet(false, true)) {
            callback?.invoke(Result.failure(IllegalStateException("Geo data update is already running.")))
            return
        }
        Thread {
            val result = runCatching {
                val downloads = listOf(
                    download(appContext, GEOSITE, MAX_GEOSITE_BYTES),
                    download(appContext, GEOIP, MAX_GEOIP_BYTES)
                )
                replaceTogether(appContext.filesDir, downloads)
                val now = System.currentTimeMillis()
                preferences(appContext).edit()
                    .putLong(KEY_LAST_SUCCESS, now)
                    .remove(KEY_LAST_ERROR)
                    .apply()
                snapshot(appContext)
            }.onFailure { error ->
                preferences(appContext).edit()
                    .putString(KEY_LAST_ERROR, error.message ?: error.javaClass.simpleName)
                    .apply()
            }
            updating.set(false)
            callback?.invoke(result)
        }.apply { name = "TcpRoute2-GeoDataUpdate" }.start()
    }

    private fun download(context: Context, name: String, maximumBytes: Long): Download {
        val checksum = readUrl("$RELEASE_BASE/$name.sha256sum", MAX_CHECKSUM_BYTES)
            .toString(Charsets.UTF_8)
            .trim()
            .split(Regex("\\s+"), limit = 2)
            .first()
            .lowercase()
        require(checksum.matches(SHA256)) { "Invalid $name checksum response." }

        val temporary = File(context.cacheDir, "$name.download")
        temporary.delete()
        try {
            val bytes = readUrl("$RELEASE_BASE/$name", maximumBytes)
            temporary.outputStream().use { it.write(bytes) }
            require(temporary.sha256() == checksum) { "$name checksum mismatch." }
            return Download(name, temporary, checksum)
        } catch (error: Throwable) {
            temporary.delete()
            throw error
        }
    }

    private fun readUrl(address: String, maximumBytes: Long): ByteArray {
        val connection = URL(address).openConnection() as HttpURLConnection
        connection.connectTimeout = 15_000
        connection.readTimeout = 45_000
        connection.instanceFollowRedirects = true
        connection.setRequestProperty("User-Agent", "TcpRoute2-Android")
        try {
            val status = connection.responseCode
            require(status in 200..299) { "Geo data server returned HTTP $status." }
            require(connection.url.protocol.equals("https", ignoreCase = true)) { "Geo data redirect must use HTTPS." }
            val length = connection.contentLengthLong
            require(length < 0 || length <= maximumBytes) { "Geo data response is too large." }
            return connection.inputStream.use { it.readLimited(maximumBytes) }
        } finally {
            connection.disconnect()
        }
    }

    private fun InputStream.readLimited(maximumBytes: Long): ByteArray {
        val output = java.io.ByteArrayOutputStream()
        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
        var total = 0L
        while (true) {
            val count = read(buffer)
            if (count < 0) break
            total += count
            require(total <= maximumBytes) { "Geo data response is too large." }
            output.write(buffer, 0, count)
        }
        return output.toByteArray()
    }

    private fun replaceTogether(directory: File, downloads: List<Download>) {
        val backups = mutableListOf<Pair<File, File>>()
        val installed = mutableListOf<File>()
        try {
            downloads.forEach { download ->
                val destination = File(directory, download.name)
                val backup = File(directory, "${download.name}.bak")
                backup.delete()
                if (destination.exists()) {
                    check(destination.renameTo(backup)) { "Unable to preserve ${download.name}." }
                    backups += destination to backup
                }
                val marker = File(directory, "${download.name}.sha256")
                val markerBackup = File(directory, "${download.name}.sha256.bak")
                markerBackup.delete()
                if (marker.exists()) {
                    check(marker.renameTo(markerBackup)) { "Unable to preserve ${download.name} checksum." }
                    backups += marker to markerBackup
                }
            }
            downloads.forEach { download ->
                val destination = File(directory, download.name)
                check(download.file.renameTo(destination)) { "Unable to install ${download.name}." }
                installed += destination
                val marker = File(directory, "${download.name}.sha256")
                marker.writeText(download.checksum)
                installed += marker
            }
            backups.forEach { (_, backup) -> backup.delete() }
        } catch (error: Throwable) {
            installed.forEach(File::delete)
            backups.asReversed().forEach { (destination, backup) -> backup.renameTo(destination) }
            throw error
        } finally {
            downloads.forEach { it.file.delete() }
        }
    }

    private fun File.sha256(): String {
        val digest = MessageDigest.getInstance("SHA-256")
        inputStream().use { input ->
            val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                digest.update(buffer, 0, count)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun preferences(context: Context) =
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    private fun Long?.orZero(): Long = this ?: 0L

    private const val PREFERENCES = "geo_data"
    private const val KEY_INTERVAL = "interval"
    private const val KEY_LAST_SUCCESS = "last_success"
    private const val KEY_LAST_ERROR = "last_error"
    private const val GEOSITE = "geosite.dat"
    private const val GEOIP = "geoip.dat"
    private const val RELEASE_BASE =
        "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download"
    private const val MAX_CHECKSUM_BYTES = 4L * 1024L
    private const val MAX_GEOSITE_BYTES = 32L * 1024L * 1024L
    private const val MAX_GEOIP_BYTES = 64L * 1024L * 1024L
    private val SHA256 = Regex("^[0-9a-f]{64}$")
    private val updating = AtomicBoolean(false)
}
