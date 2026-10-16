package io.tcproute.android

import android.content.Context
import android.util.AtomicFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.Proxy
import java.net.URL
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

data class AndroidSubscriptionNode(val id: String, val name: String, val proxyUrl: String, val options: JSONObject = JSONObject())
data class AndroidSubscription(
    val id: String = UUID.randomUUID().toString(), val name: String = "", val url: String = "",
    val nodes: List<AndroidSubscriptionNode> = emptyList(), val selected: Set<String> = emptySet(),
    val userAgent: String = "Mihomo/Clash Verge", val defaults: JSONObject = JSONObject("{\"Sleep\":80,\"Credit\":0,\"DnsResolve\":false,\"CorrectDelay\":0}"),
    val lockedFields: Set<String> = emptySet(), val overrides: JSONObject = JSONObject(),
    val excluded: Set<String> = emptySet(), val updatedAt: Long = 0, val intervalMinutes: Long = 0, val selectedNodesAll: Boolean = false,
    val managedPrefixes: Set<String> = emptySet()
) {
    val prefix: String get() = "sub_${name.ifBlank { id }.replace(Regex("[^\\p{L}\\p{N}_-]"), "_")}_"
    fun validate() {
        require(id.matches(Regex("[A-Za-z0-9_-]{1,96}")) && name.isNotBlank() && name.length <= 96)
        require(URL(url).protocol in listOf("https", "http")) { "Subscription URL must use HTTP(S)." }
        require(userAgent.length <= 512 && userAgent.none(Char::isISOControl)) { "Invalid User-Agent." }
        require(intervalMinutes == 0L || intervalMinutes in 15..525600) { "Update interval must be 0 or 15–525600 minutes." }
        SubscriptionParser.validateOptions(defaults)
        lockedFields.forEach { require(it in SubscriptionParser.optionKeys) { "Unknown locked field: $it" } }
        overrides.keys().forEach { SubscriptionParser.validateOptions(overrides.getJSONObject(it)) }
    }
}

object SubscriptionStore {
    private const val MAX_BYTES = 4 * 1024 * 1024
    private val io = Executors.newSingleThreadExecutor()
    private fun file(context: Context) = File(context.filesDir, "subscriptions.json")

    @Synchronized
    fun load(context: Context): List<AndroidSubscription> {
        val target = file(context)
        val json = if (target.exists()) AtomicFile(target).openRead().bufferedReader().use { it.readText() }
        else context.getSharedPreferences("tcproute-subscriptions", Context.MODE_PRIVATE).getString("sources", "[]") ?: "[]"
        val array = JSONArray(json)
        return (0 until array.length()).map { index ->
            val item = array.getJSONObject(index)
            val nodes = item.optJSONArray("nodes") ?: JSONArray()
            AndroidSubscription(item.getString("id"), item.optString("name", "subscription"), item.getString("url"),
                (0 until nodes.length()).map { n -> nodes.getJSONObject(n).let { AndroidSubscriptionNode(it.getString("id"), it.getString("name"), it.getString("proxyUrl"), it.optJSONObject("options") ?: JSONObject()) } },
                strings(item.optJSONArray("selected")), item.optString("userAgent"), item.optJSONObject("defaults") ?: AndroidSubscription().defaults,
                strings(item.optJSONArray("lockedFields")), item.optJSONObject("overrides") ?: JSONObject(), strings(item.optJSONArray("excluded")), item.optLong("updatedAt"), item.optLong("intervalMinutes"), item.optBoolean("selectedNodesAll", false), strings(item.optJSONArray("managedPrefixes"))).let { source -> source.copy(excluded = normalizeExcluded(source.excluded, source.nodes)) }
        }
    }

    @Synchronized
    fun save(context: Context, sources: List<AndroidSubscription>) {
        require(sources.size <= 16 && sources.map { it.id }.distinct().size == sources.size)
        sources.forEach(AndroidSubscription::validate)
        val json = JSONArray().apply { sources.forEach { source ->
            put(JSONObject().put("id", source.id).put("name", source.name).put("url", source.url).put("userAgent", source.userAgent)
                .put("managedPrefixes", JSONArray((source.managedPrefixes + source.prefix).toList()))
                .put("defaults", source.defaults).put("lockedFields", JSONArray(source.lockedFields.toList())).put("overrides", source.overrides)
                .put("selected", JSONArray(source.selected.toList())).put("excluded", JSONArray(normalizeExcluded(source.excluded, source.nodes).toList())).put("updatedAt", source.updatedAt).put("intervalMinutes", source.intervalMinutes).put("selectedNodesAll", source.selectedNodesAll)
                .put("nodes", JSONArray().apply { source.nodes.forEach { put(JSONObject().put("id", it.id).put("name", it.name).put("proxyUrl", it.proxyUrl).put("options", it.options)) } }))
        } }.toString()
        val atomic = AtomicFile(file(context)); val stream = atomic.startWrite()
        try { stream.write(json.toByteArray(Charsets.UTF_8)); atomic.finishWrite(stream) } catch (error: Exception) { atomic.failWrite(stream); throw error }
    }

    fun execute(context: Context, operation: () -> Unit, completed: (Result<Unit>) -> Unit) {
        val app = context.applicationContext
        io.execute { val result = runCatching { synchronized(this) { operation() } }; android.os.Handler(app.mainLooper).post { completed(result) } }
    }

    fun refresh(context: Context, source: AndroidSubscription) {
        source.validate()
        val nodes = download(source)
        val previous = load(context).firstOrNull { it.id == source.id } ?: source
        val oldNames = previous.nodes.associateBy { it.name }
        sync(context, source.copy(nodes = nodes.map { it.copy(id = oldNames[it.name]?.id ?: it.id) }, updatedAt = System.currentTimeMillis()), null)
    }

    fun update(context: Context, source: AndroidSubscription) { source.validate(); sync(context, source, source.selected) }
    fun delete(context: Context, source: AndroidSubscription) { sync(context, source, emptySet(), delete = true) }

    private fun sync(context: Context, source: AndroidSubscription, selection: Set<String>?, delete: Boolean = false) {
        val sources = load(context)
        val config = TcpRouteConfigStore.ensureDefaultConfig(context); val original = config.readText()
        val owners = sources + source
        val owned = SubscriptionTomlEditor.edit(context, original, includeRemoved = true, sources = owners).getJSONArray("Selections")
        val existing = (0 until owned.length()).map { owned.getJSONObject(it) }.filter { it.getString("SourceId") == source.id }
        val nameIds = existing.filter { it.optString("NodeName").isNotBlank() }.associate { it.getString("NodeName") to it.getString("NodeId") }
        val nodes = source.nodes.map { it.copy(id = nameIds[it.name] ?: it.id) }
        val excluded = normalizeExcluded(source.excluded, sources.filter { it.id == source.id }.flatMap { it.nodes } + nodes).toMutableSet()
        if (selection != null) nodes.forEach { if (it.id in selection) excluded.remove(it.id) else excluded.add(it.id) }
        val selectAll = source.selected.isEmpty() && source.selectedNodesAll
        val active = if (delete) emptyList() else nodes.filter { it.id !in excluded && (selection == null || it.id in selection || selectAll) }
        val activeIds = active.map { it.id }.toSet()
        val availableIds = nodes.map { it.id }.toSet()
        val imports = JSONArray().apply { active.forEach { node ->
            put(JSONObject().put("SourceId", source.id).put("NodeId", node.id).put("NodeName", node.name)
                .put("Upstream", upstream(source, node)).put("LockedFields", JSONArray(source.lockedFields.toList())))
        } }
        val removals = JSONArray().apply { existing.filter { it.getString("NodeId") !in activeIds }.forEach {
            put(JSONObject(it.toString()).put("Delete", !delete && it.getString("NodeId") in availableIds))
        } }
        val result = SubscriptionTomlEditor.edit(context, original, imports, removals, sources = owners, migrate = true)
        val content = result.getString("Content")
        val next = source.copy(nodes = nodes, selected = activeIds, excluded = excluded,
            managedPrefixes = source.managedPrefixes + source.prefix + sources.filter { it.id == source.id }.flatMap { it.managedPrefixes + it.prefix })
        val updated = sources.filterNot { it.id == source.id } + if (delete) emptyList() else listOf(next)
        SubscriptionTomlEditor.write(config, original, content)
        try { save(context, updated) } catch (error: Exception) {
            if (config.readText() == content) SubscriptionTomlEditor.write(config, content, original)
            throw error
        }
        SubscriptionWorker.schedule(context, source, delete)
    }

    fun upstream(source: AndroidSubscription, node: AndroidSubscriptionNode): JSONObject {
        val uri = java.net.URI(node.proxyUrl)
        val query = linkedMapOf<String, MutableList<String>>()
        uri.rawQuery?.split('&')?.filter { it.isNotEmpty() }?.forEach { part ->
            val key = java.net.URLDecoder.decode(part.substringBefore('='), "UTF-8")
            query.getOrPut(key) { mutableListOf() }.add(java.net.URLDecoder.decode(part.substringAfter('=', ""), "UTF-8"))
        }
        val credits = query.keys.filter { it.equals("credit", true) }
        val parents = query.keys.filter { it.equals("upProxy", true) }
        require(credits.size <= 1 && parents.size <= 1 && (credits + parents).all { query[it]?.size == 1 }) { "Duplicate proxy options." }
        val nodeOptions = JSONObject(node.options.toString())
        if (credits.isNotEmpty() && !nodeOptions.has("Credit")) nodeOptions.put("Credit", query.getValue(credits.single()).single().toInt())
        val values = JSONObject("{\"DnsResolve\":false,\"Credit\":0,\"Sleep\":80,\"CorrectDelay\":0}")
        val local = source.overrides.optJSONObject(node.id) ?: JSONObject()
        listOf(source.defaults, nodeOptions, local).forEach { options -> options.keys().forEach { key -> values.put(key, options.get(key)) } }
        if (parents.isNotEmpty() && !nodeOptions.has("UpProxy") && !local.has("UpProxy")) values.put("UpProxy", query.getValue(parents.single()).single())
        SubscriptionParser.validateOptions(values)
        var url = node.proxyUrl
        if (values.has("UpProxy") || credits.isNotEmpty()) {
            require(uri.scheme in listOf("http", "https", "socks5")) { "Proxy option rewriting requires an HTTP(S)/SOCKS5 node." }
            (parents + credits).forEach(query::remove)
            if (values.has("UpProxy")) query["upProxy"] = mutableListOf(values.getString("UpProxy"))
            fun encode(value: String) = java.net.URLEncoder.encode(value, "UTF-8").replace("+", "%20")
            val suffix = query.flatMap { (key, items) -> items.map { "${encode(key)}=${encode(it)}" } }.joinToString("&")
            url = node.proxyUrl.substringBefore('#').substringBefore('?') + (if (suffix.isEmpty()) "" else "?$suffix") + (if (uri.rawFragment == null) "" else "#${uri.rawFragment}")
        }
        values.remove("UpProxy")
        return values.put("Name", source.prefix + node.id).put("Alias", node.name).put("ProxyUrl", url).put("Remove", false)
    }

    internal fun download(source: AndroidSubscription): List<AndroidSubscriptionNode> {
        var address = URL(source.url)
        repeat(6) { attempt ->
            val connection = address.openConnection(Proxy.NO_PROXY) as HttpURLConnection
            try {
                connection.connectTimeout = 15_000; connection.readTimeout = 30_000; connection.instanceFollowRedirects = false
                if (source.userAgent.isNotEmpty()) connection.setRequestProperty("User-Agent", source.userAgent)
                val status = connection.responseCode
                if (status in listOf(301, 302, 303, 307, 308)) {
                    require(attempt < 5) { "Too many subscription redirects." }
                    val next = URL(address, connection.getHeaderField("Location") ?: error("Invalid redirect."))
                    require(next.protocol in listOf("http", "https") && !(address.protocol == "https" && next.protocol == "http")) { "Invalid subscription redirect." }
                    address = next
                } else {
                    require(status in 200..299) { "Subscription HTTP status $status; cached nodes retained." }
                    require(connection.contentLengthLong <= MAX_BYTES) { "Subscription exceeds 4 MiB." }
                    val bytes = connection.inputStream.use { input ->
                        val output = java.io.ByteArrayOutputStream(); val buffer = ByteArray(8192)
                        while (true) { val count = input.read(buffer); if (count < 0) break; require(output.size() + count <= MAX_BYTES) { "Subscription exceeds 4 MiB." }; output.write(buffer, 0, count) }; output.toByteArray()
                    }
                    return SubscriptionParser.parse(Charsets.UTF_8.newDecoder().decode(java.nio.ByteBuffer.wrap(bytes)).toString())
                }
            } finally { connection.disconnect() }
        }
        error("Subscription update failed; cached nodes retained.")
    }

    internal fun normalizeExcluded(excluded: Set<String>, nodes: List<AndroidSubscriptionNode>): Set<String> {
        val names = nodes.associate { it.name to it.id }
        val ids = nodes.map { it.id }.toSet()
        return excluded.map { value -> if (value in ids) value else names[value] ?: if (value.matches(Regex("[A-Za-z0-9_-]{1,96}"))) value else SubscriptionParser.stableId(value) }.toSet()
    }

    private fun strings(array: JSONArray?): Set<String> = if (array == null) emptySet() else (0 until array.length()).map { array.getString(it) }.toSet()
    fun parse(raw: String): List<AndroidSubscriptionNode> = SubscriptionParser.parse(raw)
}

object SubscriptionTomlEditor {
    fun protect(context: Context, content: String): String {
        val sources = SubscriptionStore.load(context)
        val selected = edit(context, content, includeRemoved = true, sources = sources).getJSONArray("Selections")
        val excluded = sources.associate { source -> source.id to source.nodes.filter { it.id in source.excluded || it.name in source.excluded }.map { it.id }.toSet() }
        val removals = JSONArray().apply {
            (0 until selected.length()).map { selected.getJSONObject(it) }.filter { it.getString("NodeId") in excluded[it.getString("SourceId")].orEmpty() }
                .forEach { put(JSONObject(it.toString()).put("Delete", true)) }
        }
        return edit(context, content, removals = removals, sources = sources, protectMissing = true).getString("Content")
    }

    fun edit(context: Context, content: String, imports: JSONArray = JSONArray(), removals: JSONArray = JSONArray(), includeRemoved: Boolean = false, sources: List<AndroidSubscription> = SubscriptionStore.load(context), migrate: Boolean = false, protectMissing: Boolean = false): JSONObject {
        val request = JSONObject().put("Content", content).put("Imports", imports).put("Removals", removals).put("Migrate", migrate).put("ProtectMissing", protectMissing)
        request.put("Sources", JSONArray().apply { sources.forEach { source ->
            (source.managedPrefixes + source.prefix).forEach { prefix ->
                put(JSONObject().put("SourceId", source.id).put("Prefix", prefix)
                    .put("PoolKnown", source.updatedAt > 0 || source.nodes.isNotEmpty())
                    .put("AvailableNodes", JSONArray(source.nodes.map { it.id })).put("Nodes", JSONObject().apply {
                    source.nodes.forEach { node -> put(node.id, node.name) }
                }))
            }
        } })
        request.put("IncludeRemoved", includeRemoved)
        return editExecutable(TcpRouteBinaryInstaller.install(context).absolutePath, request)
    }

    internal fun editExecutable(executable: String, request: JSONObject): JSONObject {
        val process = ProcessBuilder(executable, "--subscription-toml").start()
        val readers = Executors.newFixedThreadPool(2)
        try {
            val output = readers.submit<String> { process.inputStream.bufferedReader().use { it.readText() } }
            val error = readers.submit<String> { process.errorStream.bufferedReader().use { it.readText() } }
            process.outputStream.bufferedWriter().use { it.write(request.toString()) }
            require(process.waitFor(20, TimeUnit.SECONDS)) { "Subscription configuration edit timed out." }
            require(process.exitValue() == 0) { "Subscription configuration edit failed: ${error.get().trim()}" }
            return JSONObject(output.get())
        } finally { process.destroy(); readers.shutdownNow() }
    }

    fun write(file: File, expected: String, content: String) {
        require(file.readText() == expected) { "Configuration changed; retry the operation." }
        val atomic = AtomicFile(file); val output = atomic.startWrite()
        try { output.write(content.toByteArray(Charsets.UTF_8)); atomic.finishWrite(output) } catch (error: Exception) { atomic.failWrite(output); throw error }
    }
}
