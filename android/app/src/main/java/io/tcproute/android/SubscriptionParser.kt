package io.tcproute.android

import org.json.JSONArray
import org.json.JSONObject
import org.yaml.snakeyaml.LoaderOptions
import org.yaml.snakeyaml.Yaml
import org.yaml.snakeyaml.constructor.SafeConstructor
import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.MessageDigest
import java.util.Base64

object SubscriptionParser {
    internal val optionKeys = setOf("DnsResolve", "Credit", "Sleep", "CorrectDelay", "UpProxy", "DialProxyFor")
    internal fun stableId(value: String): String = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }.take(24)
    fun validateOptions(options: JSONObject) {
        options.keys().forEach { key ->
            require(key in optionKeys) { "Unknown subscription option: $key" }
            val value = options.get(key)
            when (key) {
                "DnsResolve" -> require(value is Boolean)
                "Credit", "Sleep", "CorrectDelay" -> require(value is Int && (key == "Credit" || value >= 0)) { "Invalid $key." }
                else -> {
                    require(value is String && value.isNotBlank() && value.none(Char::isISOControl)) { "Invalid $key." }
                    if (key == "DialProxyFor") require(value.split(',').none { it.trim() == "*" }) { "DialProxyFor does not allow the standalone '*' selector." }
                }
            }
        }
        require(!(options.has("UpProxy") && options.has("DialProxyFor"))) { "UpProxy and DialProxyFor are mutually exclusive." }
    }

    fun parse(raw: String): List<AndroidSubscriptionNode> {
        require(raw.toByteArray(Charsets.UTF_8).size <= 4 * 1024 * 1024) { "Subscription exceeds 4 MiB." }
        var text = raw.trim().removePrefix("\uFEFF").trim()
        if (!text.contains("://") && !text.startsWith('[') && !Regex("(?m)^\\s*proxies\\s*:").containsMatchIn(text)) {
            text = runCatching { String(Base64.getDecoder().decode(text.filterNot(Char::isWhitespace).replace('-', '+').replace('_', '/')), Charsets.UTF_8).trim() }.getOrDefault(text)
        }
        val nodes = when {
            Regex("(?im)^\\s*\\[Proxy]\\s*(?:#.*)?$").containsMatchIn(text) -> surge(text)
            Regex("(?m)^\\s*proxies\\s*:").containsMatchIn(text) || text.startsWith('{') -> clash(text)
            text.startsWith('[') -> {
                val array = JSONArray(text)
                (0 until array.length()).map { index ->
                    val item = array.getJSONObject(index)
                    require(item.keys().asSequence().all { it in setOf("Id", "Name", "ProxyUrl", "Options") }) { "Unknown JSON node field." }
                    node(item.optString("Id"), item.optString("Name"), item.getString("ProxyUrl"), item.optJSONObject("Options") ?: JSONObject())
                }
            }
            else -> text.lines().map(String::trim).filter { it.isNotEmpty() && !it.startsWith('#') }.map { value ->
                val name = value.substringAfter('#', "").let { URLDecoder.decode(it.replace("+", "%2B"), "UTF-8") }
                node("", name, value.substringBefore('#'))
            }
        }
        require(nodes.size <= 2048 && (nodes.isNotEmpty() || text == "[]")) { "No subscription nodes found; cached nodes retained." }
        require(nodes.map { it.name }.distinct().size == nodes.size && nodes.map { it.id }.distinct().size == nodes.size) { "Duplicate subscription node names or IDs." }
        return nodes
    }

    private fun node(id: String, label: String, url: String, options: JSONObject = JSONObject()): AndroidSubscriptionNode {
        require(url.length <= 32 * 1024 && TcpRouteConfigStore.proxyUrlError(url) == null && !url.startsWith("direct:", true)) { "Invalid or unsupported subscription proxy URL." }
        validateOptions(options)
        val vmessName = if (label.isEmpty() && url.startsWith("vmess://")) runCatching { JSONObject(String(Base64.getDecoder().decode(url.substringAfter("://").replace('-', '+').replace('_', '/')), Charsets.UTF_8)).optString("ps") }.getOrDefault("") else ""
        val identity = label.ifEmpty { vmessName }.ifEmpty { url }
        val stable = id.ifBlank { stableId(identity) }
        require(stable.matches(Regex("[A-Za-z0-9_-]{1,96}"))) { "Invalid subscription node ID." }
        val name = label.ifBlank { vmessName }.ifBlank { "${url.substringBefore("://")} ${stable.take(8)}" }
        require(name.length <= 128 && name.none(Char::isISOControl)) { "Invalid subscription node name." }
        return AndroidSubscriptionNode(stable, name, url, options)
    }

    private fun clash(text: String): List<AndroidSubscriptionNode> {
        val options = LoaderOptions().apply { codePointLimit = 4 * 1024 * 1024; nestingDepthLimit = 32; maxAliasesForCollections = 20; isAllowDuplicateKeys = false }
        val root = try { Yaml(SafeConstructor(options)).load<Any>(text) as? Map<*, *> } catch (_: Exception) { error("Invalid Clash YAML configuration.") }
        val proxies = root?.get("proxies") as? List<*> ?: error("Clash configuration needs a proxies list.")
        require(proxies.isNotEmpty() && proxies.size <= 2048) { "No Clash nodes found." }
        return proxies.mapIndexed { index, raw ->
            val values = raw as? Map<*, *> ?: error("Invalid Clash proxy at position ${index + 1}.")
            val name = values["name"]?.toString() ?: error("Clash proxy needs a name.")
            node("", name, proxyUrl(values))
        }
    }

    private fun proxyUrl(values: Map<*, *>): String {
        fun value(key: String, default: String = "") = values[key]?.toString() ?: default
        val type = value("type").lowercase()
        require(type in setOf("http", "https", "socks5", "ss", "trojan", "vmess", "vless", "hysteria2", "hy2")) { "Unsupported subscription proxy type: $type" }
        // Refuse connection-affecting features that cannot be represented by this URL adapter.
        for (key in listOf("plugin", "plugin-opts", "smux", "obfs", "obfs-password", "client-cert", "client-key", "dialer-proxy")) require(!values.containsKey(key)) { "Unsupported subscription option: $key" }
        require(value("skip-cert-verify", "false").lowercase() == "false") { "skip-cert-verify cannot be imported without certificate verification." }
        val server = value("server"); val port = value("port").toIntOrNull()
        require(server.isNotBlank() && port != null && port in 1..65535) { "Proxy needs a server and port." }
        val host = if (':' in server && !server.startsWith('[')) "[$server]" else server
        val address = "$host:$port"
        val user = value("username"); val password = value("password")
        val auth = if (user.isEmpty()) "" else "${encode(user)}:${encode(password)}@"
        if (type in listOf("http", "https", "socks5")) {
            require(value("network", "tcp") == "tcp")
            val scheme = if (type == "http" && value("tls").equals("true", true)) "https" else type
            require(!(scheme == "socks5" && value("tls").equals("true", true))) { "TLS SOCKS5 is unsupported." }
            return "$scheme://$auth$address" + if (scheme == "https" && value("sni").isNotEmpty()) "?domain=${encode(value("sni"))}" else ""
        }
        if (type == "ss") {
            val cipher = value("cipher"); require(cipher.isNotBlank() && password.isNotEmpty()) { "Shadowsocks needs cipher and password." }
            return "ss://${encode(cipher)}:${encode(password)}@$address"
        }
        val network = value("network", "tcp"); require(network in listOf("tcp", "ws", "grpc")) { "Unsupported transport: $network" }
        val ws = values["ws-opts"] as? Map<*, *> ?: emptyMap<Any, Any>()
        val headers = ws["headers"] as? Map<*, *> ?: emptyMap<Any, Any>()
        require(headers.keys.all { it.toString().equals("Host", true) } && ws.keys.all { it in listOf("path", "headers") }) { "Unsupported WebSocket options." }
        val wsHost = headers.entries.firstOrNull { it.key.toString().equals("Host", true) }?.value?.toString() ?: ""
        val sni = value("servername", value("sni", server))
        val tls = value("tls", if (type == "trojan") "true" else "false").equals("true", true)
        if (type == "vmess") {
            val payload = JSONObject().put("v", "2").put("ps", value("name")).put("add", server).put("port", port)
                .put("id", value("uuid")).put("aid", value("alterId", "0")).put("security", value("cipher", "auto"))
                .put("net", network).put("tls", if (tls) "tls" else "").put("sni", sni)
                .put("host", wsHost).put("path", ws["path"]?.toString() ?: "/")
                .put("alpn", (values["alpn"] as? List<*>)?.joinToString(",") ?: "").put("fp", value("client-fingerprint"))
            require(network != "grpc") { "VMess gRPC conversion is not supported." }
            return "vmess://" + Base64.getEncoder().encodeToString(payload.toString().toByteArray(Charsets.UTF_8))
        }
        val query = linkedMapOf("type" to network, "security" to if (tls) "tls" else "none", "sni" to sni)
        if (network == "ws") { query["path"] = ws["path"]?.toString() ?: "/"; query["host"] = wsHost }
        if (network == "grpc") query["serviceName"] = (values["grpc-opts"] as? Map<*, *>)?.get("grpc-service-name")?.toString() ?: ""
        (values["reality-opts"] as? Map<*, *>)?.let { reality ->
            require(type == "vless" && reality["public-key"]?.toString()?.isNotEmpty() == true) { "Invalid Reality options." }
            query["security"] = "reality"; query["pbk"] = reality["public-key"].toString(); query["sid"] = reality["short-id"]?.toString() ?: ""
        }
        if (values.containsKey("alpn")) query["alpn"] = (values["alpn"] as? List<*>)?.joinToString(",") ?: error("Invalid ALPN.")
        if (value("client-fingerprint").isNotEmpty()) query["fp"] = value("client-fingerprint")
        if (value("flow").isNotEmpty()) query["flow"] = value("flow")
        val credential = if (type == "vless") value("uuid") else password
        require(credential.isNotBlank()) { "Proxy credentials are missing." }
        val scheme = if (type == "hy2") "hysteria2" else type
        return "$scheme://${encode(credential)}@$address?" + query.entries.joinToString("&") { "${it.key}=${encode(it.value)}" }
    }

    private fun surge(text: String): List<AndroidSubscriptionNode> {
        var active = false
        val nodes = mutableListOf<AndroidSubscriptionNode>()
        text.lines().forEach { raw ->
            val line = raw.trim()
            if (line.startsWith('[')) { active = line.substringBefore('#').trim().equals("[Proxy]", true); return@forEach }
            if (!active || line.isBlank() || line.startsWith('#') || line.startsWith(';')) return@forEach
            require('=' in line) { "Invalid Surge proxy entry." }
            val name = line.substringBefore('=').trim().trim('"'); val fields = csv(line.substringAfter('='))
            val type = fields.firstOrNull()?.lowercase() ?: ""
            if (type in listOf("direct", "reject", "reject-drop", "reject-no-drop", "reject-tinygif")) return@forEach
            require(fields.size >= 3) { "Surge proxy needs a server and port." }
            val values = mutableMapOf<String, Any>("name" to name, "type" to type, "server" to fields[1], "port" to fields[2])
            val rest = fields.drop(3).toMutableList()
            if (type in listOf("http", "https", "socks5") && rest.size >= 2 && '=' !in rest[0]) { values["username"] = rest.removeAt(0); values["password"] = rest.removeAt(0) }
            rest.forEach { field -> require('=' in field) { "Invalid Surge option." }; val key = field.substringBefore('=').trim(); val value = field.substringAfter('=').trim().trim('"'); values[when (key) { "encrypt-method" -> "cipher"; else -> key }] = value }
            if (type == "vmess") values["uuid"] = values.remove("username") ?: ""
            if (values.remove("ws") == "true") {
                values["network"] = "ws"
                val ws = mutableMapOf<String, Any>("path" to (values.remove("ws-path") ?: "/"))
                val headers = values.remove("ws-headers")?.toString()?.split('|')?.associate { it.substringBefore(':').trim() to it.substringAfter(':').trim() } ?: emptyMap()
                ws["headers"] = headers; values["ws-opts"] = ws
            }
            require(!values.containsKey("underlying-proxy")) { "Surge underlying-proxy requires an explicit TcpRoute2 chain." }
            nodes += node("", name, proxyUrl(values))
        }
        return nodes
    }

    internal fun csv(value: String): List<String> {
        val fields = mutableListOf<String>(); val field = StringBuilder(); var quote = false; var escaped = false
        for (c in value) when {
            escaped -> { field.append(c); escaped = false }
            c == '\\' && quote -> escaped = true
            c == '"' -> quote = !quote
            c == ',' && !quote -> { fields += field.toString().trim(); field.clear() }
            else -> field.append(c)
        }
        require(!quote && !escaped) { "Invalid quoted Surge value." }; fields += field.toString().trim(); return fields
    }

    private fun encode(value: String) = URLEncoder.encode(value, "UTF-8").replace("+", "%20")
}
