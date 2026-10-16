package io.tcproute.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONObject
import java.text.DateFormat
import java.util.Date

class SubscriptionActivity : Activity() {
    private lateinit var content: LinearLayout
    private lateinit var status: TextView
    private var sources = emptyList<AndroidSubscription>()
    private var busy = false

    override fun attachBaseContext(newBase: Context) { super.attachBaseContext(LanguageManager.wrap(newBase)) }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(24), dp(20), dp(24)) }
        setContentView(ScrollView(this).apply { isFillViewport = true; addView(content) })
        status = TextView(this).apply { textSize = 14f }
        content.addView(status, full())
        reload()
    }

    private fun reload() {
        runJob {
            val saved = SubscriptionStore.load(applicationContext)
            val owned = SubscriptionTomlEditor.edit(applicationContext, TcpRouteConfigStore.ensureDefaultConfig(applicationContext).readText()).getJSONArray("Selections")
            val selected = (0 until owned.length()).map { owned.getJSONObject(it) }.groupBy { it.getString("SourceId") }
            sources = saved.map { source -> source.copy(selected = selected[source.id].orEmpty().map { it.getString("NodeId") }.toSet()) }
        }
    }

    private fun render() {
        content.removeAllViews()
        content.addView(TextView(this).apply { text = getString(R.string.subscriptions); textSize = 24f }, full())
        content.addView(status, full())
        addButton(content, getString(R.string.subscription_add)) { editSource(AndroidSubscription()) }
        if (sources.isEmpty()) content.addView(TextView(this).apply { text = getString(R.string.subscription_empty); setPadding(0, dp(20), 0, dp(20)) }, full())
        sources.forEach { source ->
            content.addView(TextView(this).apply { text = source.name; textSize = 18f; setPadding(0, dp(24), 0, dp(4)) }, full())
            val updated = if (source.updatedAt == 0L) getString(R.string.subscription_never) else DateFormat.getDateTimeInstance().format(Date(source.updatedAt))
            content.addView(TextView(this).apply { text = getString(R.string.subscription_count, source.selected.size, source.nodes.size, updated); textSize = 14f }, full())
            val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
            addButton(actions, getString(R.string.subscription_nodes), true) { chooseNodes(source) }
            addButton(actions, getString(R.string.subscription_refresh), true) { mutate { SubscriptionStore.refresh(applicationContext, source) } }
            addButton(actions, getString(R.string.subscription_edit), true) { editSource(source) }
            content.addView(actions, full())
            addButton(content, getString(R.string.subscription_delete)) {
                AlertDialog.Builder(this).setTitle(R.string.subscription_delete).setMessage(source.name)
                    .setNegativeButton(android.R.string.cancel, null).setPositiveButton(android.R.string.ok) { _, _ -> mutate { SubscriptionStore.delete(applicationContext, source) } }.show()
            }
        }
    }

    private fun editSource(source: AndroidSubscription) {
        val fields = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(8), dp(20), dp(8)) }
        fun input(label: String, value: String, multiline: Boolean = false): EditText {
            fields.addView(TextView(this).apply { text = label }, full())
            return EditText(this).apply { setText(value); textSize = 15f; isSingleLine = !multiline; if (multiline) { minLines = 3; maxLines = 6 }; fields.addView(this, full()) }
        }
        val name = input(getString(R.string.field_name), source.name)
        val url = input(getString(R.string.subscription_url), source.url).apply { inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI }
        val ua = input(getString(R.string.subscription_ua), source.userAgent)
        val interval = input(getString(R.string.subscription_interval), source.intervalMinutes.toString()).apply { inputType = InputType.TYPE_CLASS_NUMBER }
        val allNodes = CheckBox(this).apply { text = getString(R.string.subscription_all_nodes); isChecked = source.selectedNodesAll; fields.addView(this, full()) }
        val locks = mutableMapOf<String, CheckBox>()
        val values = mutableMapOf<String, EditText>()
        val dns = CheckBox(this).apply { text = "DnsResolve"; isChecked = source.defaults.optBoolean("DnsResolve") }; fields.addView(dns, full())
        for (key in listOf("DnsResolve", "Credit", "Sleep", "CorrectDelay", "UpProxy", "DialProxyFor")) {
            if (key != "DnsResolve") values[key] = input(key, if (source.defaults.has(key)) source.defaults.get(key).toString() else "")
            locks[key] = CheckBox(this).apply { text = getString(R.string.subscription_lock, key); isChecked = key in source.lockedFields; fields.addView(this, full()) }
        }
        val overrides = input(getString(R.string.subscription_overrides), source.overrides.toString(2), true)
        val dialog = AlertDialog.Builder(this).setTitle(R.string.subscription_edit).setView(ScrollView(this).apply { addView(fields) })
            .setNegativeButton(android.R.string.cancel, null).setPositiveButton(R.string.subscription_save, null).create()
        dialog.setOnShowListener { dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            runCatching {
                val defaults = JSONObject().put("DnsResolve", dns.isChecked)
                values.forEach { (key, field) ->
                    val value = field.text.toString().trim()
                    if (value.isNotEmpty()) defaults.put(key, if (key in listOf("Credit", "Sleep", "CorrectDelay")) value.toInt() else value)
                }
                source.copy(name = name.text.toString().trim(), url = url.text.toString().trim(), userAgent = ua.text.toString(), defaults = defaults,
                    lockedFields = locks.filterValues { it.isChecked }.keys, overrides = JSONObject(overrides.text.toString()), intervalMinutes = interval.text.toString().toLong(), selectedNodesAll = allNodes.isChecked).also(AndroidSubscription::validate)
            }.onSuccess { next -> dialog.dismiss(); mutate { SubscriptionStore.update(applicationContext, next) } }
                .onFailure { error -> AlertDialog.Builder(this).setMessage(error.message ?: getString(R.string.subscription_failed)).setPositiveButton(android.R.string.ok, null).show() }
        } }
        dialog.show()
    }

    private fun chooseNodes(source: AndroidSubscription) {
        val selected = source.selected.toMutableSet()
        val nodes = source.nodes
        val dialog = AlertDialog.Builder(this).setTitle(source.name)
            .setMultiChoiceItems(nodes.map { it.name }.toTypedArray(), nodes.map { it.id in selected }.toBooleanArray()) { _, index, checked ->
                if (checked) selected.add(nodes[index].id) else selected.remove(nodes[index].id)
            }.setNegativeButton(android.R.string.cancel, null)
            .setPositiveButton(R.string.subscription_save) { _, _ -> mutate { SubscriptionStore.update(applicationContext, source.copy(selected = selected)) } }
            .setNeutralButton(R.string.subscription_select_all, null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener {
                val all = selected.size != nodes.size; selected.clear(); if (all) selected.addAll(nodes.map { it.id })
                nodes.indices.forEach { dialog.listView.setItemChecked(it, all) }
            }
            dialog.listView.setOnItemLongClickListener { _, _, index, _ ->
                val node = nodes[index]
                val field = EditText(this).apply { setText((source.overrides.optJSONObject(node.id) ?: JSONObject()).toString(2)); minLines = 3; textSize = 15f }
                AlertDialog.Builder(this).setTitle(node.name).setMessage(node.id).setView(field).setNegativeButton(android.R.string.cancel, null)
                    .setPositiveButton(R.string.subscription_save) { _, _ ->
                        runCatching { JSONObject(field.text.toString()).also(SubscriptionParser::validateOptions) }.onSuccess { options ->
                            val overrides = JSONObject(source.overrides.toString()).put(node.id, options)
                            dialog.dismiss(); mutate { SubscriptionStore.update(applicationContext, source.copy(selected = selected, overrides = overrides)) }
                        }.onFailure { status.text = getString(R.string.subscription_failed) }
                    }.show()
                true
            }
        }
        dialog.show()
    }

    private fun mutate(operation: () -> Unit) {
        runJob(changed = true) {
            operation()
            sources = SubscriptionStore.load(applicationContext)
            runOnUiThread { setResult(RESULT_OK) }
        }
    }

    private fun runJob(changed: Boolean = false, operation: () -> Unit) {
        if (busy) return
        busy = true; status.text = getString(R.string.subscription_working)
        content.isEnabled = false
        SubscriptionStore.execute(applicationContext, operation) { result ->
            busy = false
            if (isFinishing || isDestroyed) return@execute
            content.isEnabled = true
            status.text = if (result.isSuccess) { if (changed) getString(R.string.subscription_saved) else "" } else getString(R.string.subscription_failed) + "\n" + (result.exceptionOrNull()?.message ?: "")
            render()
        }
    }

    private fun addButton(parent: LinearLayout, label: String, weighted: Boolean = false, action: () -> Unit) {
        parent.addView(Button(this).apply { text = label; textSize = 14f; isEnabled = !busy; setOnClickListener { if (!busy) action() } }, if (weighted) LinearLayout.LayoutParams(0, -2, 1f) else full())
    }
    private fun full() = LinearLayout.LayoutParams(-1, -2)
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
}
