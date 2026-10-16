package io.tcproute.android

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.util.Base64

val CREATABLE_PROXY_SCHEMES = listOf(
    "socks5", "http", "https", "shadowsocks", "vmess", "vless", "trojan", "wireguard", "hysteria2"
)

data class EditableTcpRouteConfig(
    val listenPort: Int = 7070,
    val upstreams: List<EditableUpstream> = listOf(EditableUpstream.direct()),
    val hosts: List<EditableHostSource> = emptyList(),
    val dnsToml: String = ""
)

data class EditableHostSource(
    val path: String,
    val sourceType: String = SOURCE_LOCAL,
    val updateInterval: String = "24h",
    val type: String = "base",
    val credit: Int = 0
) {
    companion object {
        fun template(sourceType: String = SOURCE_LOCAL): EditableHostSource =
            EditableHostSource("", sourceType, "24h", "base", 0)
    }
}

data class EditableDomainList(
    val path: String,
    val sourceType: String = SOURCE_LOCAL,
    val type: String = "base",
    val updateInterval: String = "24h"
) {
    companion object {
        fun template(sourceType: String = SOURCE_LOCAL): EditableDomainList =
            EditableDomainList(
                path = when (sourceType) {
                    SOURCE_GEOSITE -> "geosite.dat"
                    SOURCE_GEOIP -> "geoip.dat"
                    else -> ""
                },
                sourceType = sourceType,
                type = when (sourceType) {
                    SOURCE_GEOSITE -> "gfw"
                    SOURCE_GEOIP -> "cn,private"
                    else -> "base"
                },
                updateInterval = "24h"
            )
    }
}

data class EditableUpstream(
    val name: String,
    val proxyUrl: String,
    val dnsResolve: Boolean,
    val credit: Int,
    val sleep: Int,
    val correctDelay: Int,
    val whitelist: List<EditableDomainList> = emptyList(),
    val blacklist: List<EditableDomainList> = emptyList(),
    val dialProxyFor: String = "",
    val remove: Boolean = false,
    val alias: String = ""
) {
    companion object {
        fun direct(): EditableUpstream = EditableUpstream(
            name = "direct",
            proxyUrl = "direct://0.0.0.0:0000",
            dnsResolve = true,
            credit = 0,
            sleep = 0,
            correctDelay = 0
        )

        fun proxyTemplate(scheme: String = "socks5"): EditableUpstream = EditableUpstream(
            name = scheme,
            proxyUrl = when (scheme) {
                "http" -> "http://127.0.0.1:8080"
                "https" -> "https://127.0.0.1:8080"
                "shadowsocks" -> "shadowsocks://aes-256-gcm:password@127.0.0.1:8388"
                "vmess" -> "vmess://00000000-0000-0000-0000-000000000000@127.0.0.1:443?net=ws&tls=tls"
                "vless" -> "vless://00000000-0000-0000-0000-000000000000@127.0.0.1:443?encryption=none&security=tls"
                "trojan" -> "trojan://password@127.0.0.1:443?security=tls"
                "wireguard" -> "wireguard://privateKey@127.0.0.1:2408?publicKey=peerPublicKey&address=172.16.0.2%2F32"
                "hysteria2" -> "hysteria2://password@127.0.0.1:443"
                else -> "socks5://127.0.0.1:1080"
            },
            dnsResolve = false,
            credit = 0,
            sleep = 80,
            correctDelay = 0
        )
    }
}

object TcpRouteConfigStore {
    private val addrRegex = Regex("""(?m)^\s*addr\s*=\s*["']([^"']+)["']""")
    private const val MAX_CONFIG_BYTES = 2 * 1024 * 1024

    fun ensureDefaultConfig(context: Context): File {
        val configDir = File(context.filesDir, "configs").apply { mkdirs() }
        val config = File(configDir, "default.toml")
        if (!config.exists()) {
            config.writeText(
                """
                addr="127.0.0.1:7070"

                [[UpStreams]]
                Name="direct"
                ProxyUrl="direct://0.0.0.0:0000"
                DnsResolve=true
                Credit=0
                Sleep=0
                CorrectDelay=0
                """.trimIndent()
            )
        }
        return config
    }

    fun loadEditableConfig(context: Context): EditableTcpRouteConfig =
        parseEditableConfig(ensureDefaultConfig(context).readText())

    fun saveEditableConfig(context: Context, config: EditableTcpRouteConfig): File {
        validate(config)
        val file = ensureDefaultConfig(context)
        val original = file.readText()
        val rendered = renderEditableConfig(config)
        SubscriptionTomlEditor.write(file, original, rendered)
        return file
    }

    fun loadRawConfig(context: Context): String =
        stripLegacyGeneratedComment(ensureDefaultConfig(context).readText())

    fun stripLegacyGeneratedComment(content: String): String = content.replace(
        Regex("""(?m)^# TcpRoute config generated by TcpRoute (?:Android|client)\r?\n?"""),
        ""
    )

    fun saveRawConfig(context: Context, content: String): File {
        parseEditableConfig(content)
        return ensureDefaultConfig(context).apply { writeText(content) }
    }

    fun parseEditableConfig(content: String): EditableTcpRouteConfig {
        val config = EditableTcpRouteConfig(
            listenPort = readListenPort(content),
            upstreams = parseNestedUpstreams(content).ifEmpty { listOf(EditableUpstream.direct()) },
            hosts = parseHosts(content),
            dnsToml = extractDnsToml(content)
        )
        validate(config)
        return config
    }

    fun writeRuntimeConfig(context: Context, config: File, allowLan: Boolean): File {
        val original = config.readText()
        val protectedContent = SubscriptionTomlEditor.protect(context, original)
        if (protectedContent != original) SubscriptionTomlEditor.write(config, original, protectedContent)
        val host = if (allowLan) "0.0.0.0" else "127.0.0.1"
        val runtime = replaceListenAddress(config.readText(), host, readListenPort(config))
        return File(config.parentFile, ".tcproute-runtime.toml").apply { writeText(runtime) }
    }

    fun importRuleSource(context: Context, uri: Uri): String {
        val configDir = ensureDefaultConfig(context).parentFile
            ?: error("Config directory is unavailable.")
        val rulesDir = File(configDir, "rules").apply { mkdirs() }
        val displayName = context.contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME),
            null,
            null,
            null
        )?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getString(0) else null
        }.orEmpty()
        val safeName = displayName
            .ifBlank { "rules-${System.currentTimeMillis()}.txt" }
            .replace(Regex("[^A-Za-z0-9._-]"), "_")
            .take(96)
            .ifBlank { "rules-${System.currentTimeMillis()}.txt" }
        val target = uniqueFile(rulesDir, safeName)
        context.contentResolver.openInputStream(uri)?.use { input ->
            target.outputStream().use { output -> input.copyTo(output) }
        } ?: error("Unable to read the selected rule file.")
        return "rules/${target.name}"
    }

    fun importHostSource(context: Context, uri: Uri): String {
        val configDir = ensureDefaultConfig(context).parentFile
            ?: error("Config directory is unavailable.")
        val hostsDir = File(configDir, "hosts").apply { mkdirs() }
        val displayName = context.contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME),
            null,
            null,
            null
        )?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getString(0) else null
        }.orEmpty()
        val safeName = displayName
            .ifBlank { "hosts-${System.currentTimeMillis()}.txt" }
            .replace(Regex("[^A-Za-z0-9._-]"), "_")
            .take(96)
            .ifBlank { "hosts-${System.currentTimeMillis()}.txt" }
        val target = uniqueFile(hostsDir, safeName)
        context.contentResolver.openInputStream(uri)?.use { input ->
            target.outputStream().use { output -> input.copyTo(output) }
        } ?: error("Unable to read the selected hosts file.")
        return "hosts/${target.name}"
    }

    fun readImportedConfig(context: Context, uri: Uri): String =
        context.contentResolver.openInputStream(uri)?.use { input ->
            readLimitedConfig(input)
        } ?: error("Unable to read the selected config file.")

    fun writeExportedConfig(context: Context, uri: Uri, content: String) {
        context.contentResolver.openOutputStream(uri, "wt")?.bufferedWriter(Charsets.UTF_8)?.use {
            it.write(content)
        } ?: error("Unable to write the selected config file.")
    }

    fun downloadConfig(source: String): String {
        val parsed = URI(source.trim())
        require(parsed.scheme?.lowercase() in setOf("http", "https") && !parsed.host.isNullOrBlank()) {
            "Config URL must use HTTP or HTTPS."
        }
        val connection = URL(parsed.toString()).openConnection() as HttpURLConnection
        connection.connectTimeout = 10_000
        connection.readTimeout = 15_000
        connection.instanceFollowRedirects = true
        connection.setRequestProperty("Accept", "text/plain, application/toml, */*")
        return try {
            require(connection.responseCode in 200..299) {
                "Remote server returned HTTP ${connection.responseCode}."
            }
            connection.inputStream.use { input ->
                readLimitedConfig(input)
            }.also(::parseEditableConfig)
        } finally {
            connection.disconnect()
        }
    }

    fun readListenPort(file: File): Int = readListenPort(file.readText())

    fun readListenPort(content: String): Int {
        val value = addrRegex.find(stripLineComments(content))?.groupValues?.get(1)
            ?: error("TcpRoute2 config must contain an addr value.")
        return value.substringAfterLast(':').toIntOrNull()
            ?: error("Invalid TcpRoute2 listen address: $value")
    }

    private fun replaceListenAddress(content: String, host: String, port: Int): String {
        val newline = if (content.contains("\r\n")) "\r\n" else "\n"
        val lines = content.replace("\r\n", "\n").split("\n").toMutableList()
        lines.forEachIndexed { index, line ->
            if (addrRegex.find(stripLineComment(line)) == null) return@forEachIndexed
            val indent = line.takeWhile { it == ' ' || it == '\t' }
            lines[index] = "${indent}addr=\"$host:$port\""
            return lines.joinToString(newline)
        }
        error("TcpRoute2 config must contain an addr value.")
    }

    private fun validate(config: EditableTcpRouteConfig) {
        require(config.listenPort in 1..65535) { "Listen port must be between 1 and 65535." }
        require(config.upstreams.isNotEmpty()) { "Add at least one upstream." }
        config.upstreams.forEach { upstream ->
            val name = upstream.name.trim()
            require(name.isNotEmpty()) { "Each upstream needs a name." }
            proxyUrlError(upstream.proxyUrl)?.let { error("Upstream $name: $it") }
            validateLists(name, "whitelist", upstream.whitelist)
            validateLists(name, "blacklist", upstream.blacklist)
        }
        val names = config.upstreams.map { it.name.trim() }
        require(names.distinct().size == names.size) { "Upstream names must be unique." }
        config.hosts.forEach { entry ->
            require(entry.path.trim().isNotEmpty()) { "Each host source needs a file or URL." }
            require(entry.sourceType in setOf(SOURCE_LOCAL, SOURCE_REMOTE)) { "Invalid host source type." }
            require(entry.sourceType != SOURCE_REMOTE || isRemoteUrl(entry.path)) {
                "Remote host source needs an HTTP or HTTPS URL."
            }
            require(entry.type.trim().lowercase() in RULE_TYPES) {
                "Host source must use base, suffix, pan, or regex."
            }
            if (entry.sourceType == SOURCE_REMOTE) validateUpdateInterval("Host source", entry.updateInterval)
        }
    }

    fun proxyUrlError(value: String): String? {
        val text = value.trim()
        if (text.isEmpty() || text.any(Char::isWhitespace)) return "Proxy URL is empty or contains whitespace."
        val uri = runCatching { URI(text) }.getOrNull() ?: return "Proxy URL is malformed."
        val scheme = uri.scheme?.lowercase() ?: return "Proxy URL needs a scheme."
        if (scheme !in PROXY_SCHEMES) return "Unsupported proxy type: $scheme."
        if (scheme == "direct") return if (uri.rawSchemeSpecificPart.isNullOrBlank()) "Direct URL is incomplete." else null

        val payload = text.substringAfter("://", "").substringBefore('#')
        if (scheme == "vmess" && !payload.contains('@') && uri.port < 0) {
            val decoded = decodeBase64(payload) ?: return "VMess URL payload is not valid Base64."
            if (decoded.trim().startsWith("{")) {
                val json = runCatching { JSONObject(decoded) }.getOrNull()
                    ?: return "VMess JSON payload is malformed."
                if (json.optString("add").isBlank() || json.optString("id").isBlank() ||
                    json.optString("port").toIntOrNull() !in 1..65535
                ) return "VMess JSON payload is incomplete."
            } else if (!decoded.contains('@') || !decoded.contains(':')) {
                return "VMess URL payload is incomplete."
            }
            return null
        }
        if (scheme in setOf("shadowsocks", "ss") && !payload.contains('@') && uri.port < 0) {
            val decoded = decodeBase64(payload) ?: return "Shadowsocks URL payload is not valid Base64."
            if (!decoded.contains(':') || !decoded.contains('@')) return "Shadowsocks URL payload is incomplete."
            return null
        }
        if (scheme in setOf("vless", "trojan", "wireguard", "hysteria2", "hysteria", "hy2") &&
            !payload.contains('@') && uri.port < 0
        ) {
            val decoded = decodeBase64(payload) ?: return "Encoded proxy URL payload is not valid Base64."
            if (!decoded.contains('@') || !decoded.contains(':')) return "Encoded proxy URL payload is incomplete."
            return null
        }
        if (uri.host.isNullOrBlank()) return "Proxy URL needs a host."
        val port = uri.port
        if (port !in 1..65535 && scheme !in setOf("hysteria2", "hysteria", "hy2")) {
            return "Proxy URL needs a valid port."
        }
        if (scheme in setOf("socks5", "http", "https") && uri.rawPath !in setOf("", null)) {
            return "This proxy type does not support a URL path."
        }
        if (scheme in setOf("shadowsocks", "ss") &&
            (uri.rawUserInfo.isNullOrBlank() || !uri.rawUserInfo.orEmpty().contains(':'))
        ) return "Shadowsocks URL needs a method and password."
        if (scheme in setOf("vmess", "vless", "trojan", "wireguard") && uri.rawUserInfo.isNullOrBlank()) {
            return "This proxy type needs credentials before the host."
        }
        if (scheme == "wireguard") {
            val query = uri.rawQuery.orEmpty()
            if (!query.contains("publicKey=", ignoreCase = true) || !query.contains("address=", ignoreCase = true)) {
                return "WireGuard URL needs publicKey and address parameters."
            }
        }
        return null
    }

    private fun validateLists(upstream: String, listName: String, lists: List<EditableDomainList>) {
        lists.forEachIndexed { index, rule ->
            val label = "$upstream $listName ${index + 1}"
            val type = rule.type.trim().lowercase()
            require(rule.sourceType in SOURCE_TYPES) { "$label has an invalid source type." }
            require(rule.path.trim().isNotEmpty()) { "$label needs a Path." }
            if (rule.sourceType == SOURCE_GEOSITE || rule.sourceType == SOURCE_GEOIP) {
                val kind = if (rule.sourceType == SOURCE_GEOSITE) "Geosite" else "GeoIP"
                val example = if (rule.sourceType == SOURCE_GEOSITE) "gfw,google,bing" else "cn,private"
                require(normalizeGeoRules(type) != null) { "$label needs $kind rules such as $example." }
                if (isRemoteUrl(rule.path)) validateUpdateInterval(label, rule.updateInterval)
                return@forEachIndexed
            }
            require(type in RULE_TYPES) { "$label must use base, suffix, pan, or regex." }
            if (rule.sourceType == SOURCE_REMOTE) {
                require(isRemoteUrl(rule.path)) { "$label remote source needs an HTTP or HTTPS URL." }
                validateUpdateInterval(label, rule.updateInterval)
            }
        }
    }

    fun renderEditableConfig(config: EditableTcpRouteConfig): String {
        validate(config)
        return buildString {
            appendLine("addr=\"127.0.0.1:${config.listenPort}\"")
            appendLine()
            if (config.dnsToml.isNotEmpty()) appendLine(config.dnsToml)
            config.hosts.forEach { entry ->
                appendLine("[[Hosts]]")
                appendLine("Path=${tomlString(entry.path.trim())}")
                appendLine("Type=${tomlString(entry.type.trim().lowercase())}")
                appendLine("Credit=${entry.credit}")
                if (entry.sourceType == SOURCE_REMOTE && entry.updateInterval.isNotBlank()) {
                    appendLine("UpdateInterval=${tomlString(entry.updateInterval.trim())}")
                }
                appendLine()
            }
            config.upstreams.forEach { upstream ->
                appendLine("[[UpStreams]]")
                appendLine("Name=${tomlString(upstream.name.trim())}")
                if (upstream.alias.isNotEmpty()) appendLine("Alias=${tomlString(upstream.alias)}")
                appendLine("ProxyUrl=${tomlString(upstream.proxyUrl.trim())}")
                if (upstream.dialProxyFor.isNotEmpty()) appendLine("DialProxyFor=${tomlString(upstream.dialProxyFor)}")
                if (upstream.remove) appendLine("Remove=true")
                appendLine("DnsResolve=${upstream.dnsResolve}")
                appendLine("Credit=${upstream.credit}")
                appendLine("Sleep=${upstream.sleep}")
                appendLine("CorrectDelay=${upstream.correctDelay}")
                appendLine()
                upstream.whitelist.forEach { appendDomainList("Whitelist", it) }
                upstream.blacklist.forEach { appendDomainList("Blacklist", it) }
            }
        }
    }

    private fun StringBuilder.appendDomainList(name: String, rule: EditableDomainList) {
        appendLine("[[UpStreams.$name]]")
        if ((rule.sourceType == SOURCE_GEOSITE && rule.path.trim() == "geosite.dat") ||
            (rule.sourceType == SOURCE_GEOIP && rule.path.trim() == "geoip.dat")) {
            val kind = if (rule.sourceType == SOURCE_GEOIP) "geoip" else "geosite"
            appendLine("Type=${tomlString(kind)}")
            val selectors = normalizeGeoRules(rule.type)!!.split(',').joinToString(",") { "$kind:$it" }
            appendLine("Path=${tomlString(selectors)}")
        } else if (rule.sourceType == SOURCE_GEOSITE || rule.sourceType == SOURCE_GEOIP) {
            val prefix = if (rule.sourceType == SOURCE_GEOSITE) "geosite" else "geoip"
            appendLine("Type=${tomlString("$prefix:${normalizeGeoRules(rule.type)!!}")}")
            appendLine("Path=${tomlString(rule.path.trim())}")
            if (isRemoteUrl(rule.path) && rule.updateInterval.isNotBlank()) {
                appendLine("UpdateInterval=${tomlString(rule.updateInterval.trim())}")
            }
        } else {
            appendLine("Path=${tomlString(rule.path.trim())}")
            appendLine("Type=${tomlString(rule.type.trim().lowercase())}")
            if (rule.sourceType == SOURCE_REMOTE && rule.updateInterval.isNotBlank()) {
                appendLine("UpdateInterval=${tomlString(rule.updateInterval.trim())}")
            }
        }
        appendLine()
    }

    private fun parseNestedUpstreams(content: String): List<EditableUpstream> {
        data class MutableUpstream(
            val values: MutableList<String> = mutableListOf(),
            val whitelist: MutableList<EditableDomainList> = mutableListOf(),
            val blacklist: MutableList<EditableDomainList> = mutableListOf()
        )

        val result = mutableListOf<EditableUpstream>()
        var upstream: MutableUpstream? = null
        var listKind: String? = null
        var listLines = mutableListOf<String>()

        fun flushList() {
            val current = upstream ?: return
            val kind = listKind ?: return
            val parsed = parseDomainListBlock(listLines)
            if (kind == "whitelist") current.whitelist.add(parsed) else current.blacklist.add(parsed)
            listKind = null
            listLines = mutableListOf()
        }
        fun flushUpstream() {
            flushList()
            val current = upstream ?: return
            val values = parseBlockValues(current.values)
            result.add(
                EditableUpstream(
                    name = values["Name"] ?: "proxy",
                    alias = values["Alias"] ?: "",
                    proxyUrl = values["ProxyUrl"] ?: "socks5://127.0.0.1:1080",
                    dialProxyFor = values["DialProxyFor"] ?: "",
                    remove = values["Remove"]?.toBooleanStrictOrNull() ?: false,
                    dnsResolve = values["DnsResolve"]?.toBooleanStrictOrNull() ?: false,
                    credit = values["Credit"]?.toIntOrNull() ?: 0,
                    sleep = values["Sleep"]?.toIntOrNull() ?: 0,
                    correctDelay = values["CorrectDelay"]?.toIntOrNull() ?: 0,
                    whitelist = current.whitelist.toList(),
                    blacklist = current.blacklist.toList()
                )
            )
            upstream = null
        }

        content.lineSequence().forEach { line ->
            when (stripLineComment(line).trim()) {
                "[[UpStreams]]" -> {
                    flushUpstream()
                    upstream = MutableUpstream()
                }
                "[[UpStreams.Whitelist]]" -> {
                    flushList()
                    if (upstream != null) listKind = "whitelist"
                }
                "[[UpStreams.Blacklist]]" -> {
                    flushList()
                    if (upstream != null) listKind = "blacklist"
                }
                else -> {
                    if (line.trim().startsWith("[[") && line.trim().endsWith("]]")) {
                        flushUpstream()
                    } else if (listKind != null) {
                        listLines.add(line)
                    } else {
                        upstream?.values?.add(line)
                    }
                }
            }
        }
        flushUpstream()
        return result
    }

    private fun parseHosts(content: String): List<EditableHostSource> {
        val result = mutableListOf<EditableHostSource>()
        var lines: MutableList<String>? = null
        fun flush() {
            val current = lines ?: return
            val values = parseBlockValues(current)
            val path = values["Path"].orEmpty()
            result.add(
                EditableHostSource(
                    path = path,
                    sourceType = if (isRemoteUrl(path)) SOURCE_REMOTE else SOURCE_LOCAL,
                    updateInterval = values["UpdateInterval"] ?: "24h",
                    type = values["Type"]?.lowercase()?.takeIf { it in RULE_TYPES } ?: "base",
                    credit = values["Credit"]?.toIntOrNull() ?: 0
                )
            )
            lines = null
        }
        content.lineSequence().forEach { line ->
            val trimmed = line.trim()
            when {
                trimmed == "[[Hosts]]" -> {
                    flush()
                    lines = mutableListOf()
                }
                trimmed.startsWith('[') && trimmed.endsWith(']') -> flush()
                lines != null -> lines?.add(line)
            }
        }
        flush()
        return result
    }

    private fun extractDnsToml(content: String): String {
        var inDns = false
        return content.lineSequence().filter { line ->
            if (line.trim().startsWith("[")) inDns = line.substringBefore('#').trim() == "[[Dns]]"
            inDns
        }.joinToString("\n")
    }

    private fun parseDomainListBlock(lines: List<String>): EditableDomainList {
        val values = parseBlockValues(lines)
        val rawPath = values["Path"].orEmpty().trim()
        val rawType = values["Type"].orEmpty().trim().lowercase()
        // Migrate both preview formats that emitted only one Geosite field.
        val geoReference = rawType.takeIf(::isGeoReference)
            ?: rawPath.takeIf(::isGeoReference)
        val geoIP = geoReference?.startsWith("geoip:", ignoreCase = true) == true
        val path = if (geoReference == null) {
            rawPath
        } else {
            rawPath.takeUnless { it.isEmpty() || isGeoReference(it) }
                ?: if (geoIP) "geoip.dat" else "geosite.dat"
        }
        val type = geoReference?.let(::geoRulesFromReference)
            ?: rawType.takeIf { it in RULE_TYPES } ?: "base"
        return EditableDomainList(
            path = path,
            sourceType = when {
                geoIP -> SOURCE_GEOIP
                geoReference != null -> SOURCE_GEOSITE
                else -> inferSourceType(path)
            },
            type = type,
            updateInterval = values["UpdateInterval"] ?: "24h"
        )
    }

    private fun parseBlockValues(lines: List<String>): Map<String, String> {
        val values = mutableMapOf<String, String>()
        lines.forEach { rawLine ->
            val line = stripLineComment(rawLine)
            val separator = line.indexOf('=')
            if (separator < 0) return@forEach
            values[line.substring(0, separator).trim()] = unquote(line.substring(separator + 1).trim())
        }
        return values
    }

    private fun stripLineComments(content: String): String =
        content.lineSequence().joinToString("\n") { stripLineComment(it) }

    private fun tomlString(value: String): String =
        "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""

    private fun unquote(value: String): String {
        val trimmed = value.trim()
        if (trimmed.length < 2 || trimmed.first() != trimmed.last() || trimmed.first() !in setOf('"', '\'')) {
            return trimmed
        }
        return trimmed.drop(1).dropLast(1).replace("\\\"", "\"").replace("\\\\", "\\")
    }

    private fun stripLineComment(line: String): String {
        var inSingleQuote = false
        var inDoubleQuote = false
        var escaped = false
        line.forEachIndexed { index, character ->
            if (character == '\\' && !escaped) {
                escaped = true
                return@forEachIndexed
            }
            if (character == '"' && !inSingleQuote && !escaped) inDoubleQuote = !inDoubleQuote
            else if (character == '\'' && !inDoubleQuote && !escaped) inSingleQuote = !inSingleQuote
            else if (character == '#' && !inSingleQuote && !inDoubleQuote) return line.take(index)
            escaped = false
        }
        return line
    }

    private fun uniqueFile(directory: File, name: String): File {
        val initial = File(directory, name)
        if (!initial.exists()) return initial
        val extensionIndex = name.lastIndexOf('.')
        val base = if (extensionIndex > 0) name.substring(0, extensionIndex) else name
        val extension = if (extensionIndex > 0) name.substring(extensionIndex) else ""
        var index = 2
        while (true) {
            val candidate = File(directory, "$base-$index$extension")
            if (!candidate.exists()) return candidate
            index++
        }
    }

    private fun decodeBase64(value: String): String? {
        val normalized = value.replace('-', '+').replace('_', '/')
        val padded = normalized + "=".repeat((4 - normalized.length % 4) % 4)
        return runCatching { String(Base64.getDecoder().decode(padded), Charsets.UTF_8) }.getOrNull()
    }

    private fun inferSourceType(path: String): String = when {
        isRemoteUrl(path) -> SOURCE_REMOTE
        else -> SOURCE_LOCAL
    }

    private fun isGeoSiteReference(type: String): Boolean =
        type.trim().startsWith("geosite:", ignoreCase = true) && geoRulesFromReference(type) != null

    private fun isGeoIPReference(type: String): Boolean =
        type.trim().startsWith("geoip:", ignoreCase = true) && geoRulesFromReference(type) != null

    private fun isGeoReference(type: String): Boolean = isGeoSiteReference(type) || isGeoIPReference(type)

    private fun geoRulesFromReference(value: String): String? =
        value.trim().substringAfter(':', "").let(::normalizeGeoRules)

    private fun normalizeGeoRules(value: String): String? {
        val selector = Regex("""(?i)^[^\\/:,@\s]+(?:@[^\\/:,@\s]+)*$""")
        val text = value.trim()
        val rawRules = if (text.startsWith("geosite:", ignoreCase = true) ||
            text.startsWith("geoip:", ignoreCase = true)
        ) text.substringAfter(':') else text
        val rules = rawRules.split(',').map { it.trim().lowercase().removePrefix("geosite:").removePrefix("geoip:") }
        return rules.takeIf { it.isNotEmpty() && it.all(selector::matches) }?.joinToString(",")
    }

    private fun isRemoteUrl(path: String): Boolean = runCatching { URI(path.trim()) }.getOrNull()?.let {
        it.scheme?.lowercase() in setOf("http", "https") && !it.host.isNullOrBlank()
    } == true

    private fun validateUpdateInterval(label: String, value: String) {
        val text = value.trim()
        require(text.isNotEmpty() && goDurationSeconds(text)?.let { it >= 60.0 } == true) {
            "$label has an invalid update interval (minimum 1m)."
        }
    }

    private fun goDurationSeconds(value: String): Double? {
        val token = Regex("""(\d+(?:\.\d+)?)(ns|us|µs|μs|ms|s|m|h)""")
        val matches = token.findAll(value).toList()
        if (matches.isEmpty() || matches.joinToString("") { it.value } != value) return null
        return matches.sumOf {
            val amount = it.groupValues[1].toDoubleOrNull() ?: return null
            amount * when (it.groupValues[2]) {
                "ns" -> 1e-9
                "us", "µs", "μs" -> 1e-6
                "ms" -> 1e-3
                "s" -> 1.0
                "m" -> 60.0
                "h" -> 3600.0
                else -> return null
            }
        }
    }

    private fun readLimitedConfig(input: java.io.InputStream): String {
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        var total = 0
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            total += count
            require(total <= MAX_CONFIG_BYTES) { "Config exceeds $MAX_CONFIG_BYTES bytes." }
            output.write(buffer, 0, count)
        }
        return output.toString(Charsets.UTF_8.name())
    }

    private val RULE_TYPES = setOf("base", "suffix", "pan", "regex")
    private val SOURCE_TYPES = setOf(SOURCE_LOCAL, SOURCE_REMOTE, SOURCE_GEOSITE, SOURCE_GEOIP)
    private val PROXY_SCHEMES = setOf(
        "direct", "socks5", "http", "https", "shadowsocks", "ss",
        "vmess", "vless", "trojan", "wireguard", "hysteria2", "hysteria", "hy2"
    )
}

const val SOURCE_LOCAL = "local"
const val SOURCE_REMOTE = "remote"
const val SOURCE_GEOSITE = "geosite"
const val SOURCE_GEOIP = "geoip"
