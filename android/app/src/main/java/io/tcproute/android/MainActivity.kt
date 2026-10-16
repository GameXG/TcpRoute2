package io.tcproute.android

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import android.widget.ArrayAdapter
import android.widget.AdapterView
import android.text.format.Formatter
import java.text.DateFormat
import java.util.Date
import java.util.Locale

class MainActivity : Activity() {
    private lateinit var pageRoot: ScrollView
    private lateinit var status: TextView
    private lateinit var statusEyebrow: TextView
    private lateinit var statusDot: View
    private lateinit var endpointChip: TextView
    private lateinit var topMenuButton: TextView
    private lateinit var lanMode: CheckBox
    private lateinit var systemProxy: CheckBox
    private lateinit var allowStun: CheckBox
    private lateinit var allowQuic: CheckBox
    private lateinit var enableIpv6: CheckBox
    private lateinit var upstreamsContainer: LinearLayout
    private lateinit var ruleSetsContainer: LinearLayout
    private lateinit var routingUpstreamSpinner: Spinner
    private lateinit var routingRulesEditor: LinearLayout
    private lateinit var hostsEditor: LinearLayout
    private lateinit var hostsContainer: LinearLayout
    private lateinit var vpnToggleButton: Button
    private lateinit var socksToggleButton: Button
    private lateinit var requestLogButton: Button
    private lateinit var routingSummary: TextView
    private lateinit var routingModeGroup: RadioGroup
    private lateinit var selectAppsButton: Button
    private lateinit var mainContentRoot: ViewGroup
    private var expandedLayoutRoot: LinearLayout? = null
    private var embeddedLogPresenter: RequestLogPresenter? = null
    private var systemBarsTarget: View? = null
    private var activityStarted = false
    private val upstreamRows = mutableListOf<UpstreamRow>()
    private val ruleRowsByUpstream = mutableMapOf<UpstreamRow, MutableList<RuleSetRow>>()
    private val hostRows = mutableListOf<HostSourceRow>()
    private var refreshingRuleUpstreams = false
    private var selectedRoutingUpstream: UpstreamRow? = null
    private var pendingRuleFileRow: RuleSetRow? = null
    private var pendingHostFileRow: HostSourceRow? = null
    private var vpnState: String? = null
    private var socksState: String? = null
    private var quickListenPort = 7070
    private var quickDnsToml = ""
    private var routingPolicy = AppRoutingPolicy(AppRoutingMode.ALL, emptySet())
    private var installedApps: List<InstalledApp> = emptyList()
    private val connectionStatusReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                TcpRouteVpnService.ACTION_STATUS ->
                    renderVpnStatus(intent.getStringExtra(TcpRouteVpnService.EXTRA_STATE))
                TcpRouteService.ACTION_STATUS ->
                    renderSocksStatus(intent.getStringExtra(TcpRouteService.EXTRA_STATE))
            }
        }
    }

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(LanguageManager.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        mainContentRoot = findViewById(android.R.id.content)
        bindViews()
        updateResponsiveLayout(resources.configuration.screenWidthDp >= EXPANDED_LAYOUT_MIN_WIDTH_DP)
        observeWindowWidth()
        configureSystemBars()
        bindActions()
        loadConfigEditor()
        loadAppRoutingControls()
        loadVpnFeatureControls()
        showStatus(R.string.status_ready_title, R.string.status_ready_message, R.color.teal)
        Thread {
            TcpRouteBinaryInstaller.installGeoData(applicationContext)
            GeoDataManager.updateIfDue(applicationContext)
        }.apply { name = "TcpRoute2-GeoDataInstall" }.start()
        pageRoot.post { pageRoot.scrollTo(0, 0) }
    }

    override fun onStart() {
        super.onStart()
        activityStarted = true
        embeddedLogPresenter?.start()
        val filter = IntentFilter(TcpRouteVpnService.ACTION_STATUS).apply {
            addAction(TcpRouteService.ACTION_STATUS)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(connectionStatusReceiver, filter, RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            registerReceiver(connectionStatusReceiver, filter)
        }
        val currentVpn = TcpRouteVpnService.currentState()
        val currentSocks = TcpRouteService.currentState()
        vpnState = currentVpn
        socksState = currentSocks
        when {
            currentVpn == TcpRouteVpnService.STATE_STARTING || currentVpn == TcpRouteVpnService.STATE_RUNNING ->
                renderVpnStatus(currentVpn)
            currentSocks == TcpRouteService.STATE_STARTING || currentSocks == TcpRouteService.STATE_RUNNING ->
                renderSocksStatus(currentSocks)
            currentVpn == TcpRouteVpnService.STATE_ERROR -> renderVpnStatus(currentVpn)
            currentSocks == TcpRouteService.STATE_ERROR -> renderSocksStatus(currentSocks)
            else -> renderConnectionButtons()
        }
    }

    override fun onStop() {
        activityStarted = false
        embeddedLogPresenter?.stop()
        unregisterReceiver(connectionStatusReceiver)
        super.onStop()
    }

    override fun onDestroy() {
        embeddedLogPresenter?.destroy()
        super.onDestroy()
    }

    @Deprecated("Deprecated in Android, retained for minSdk compatibility")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 4089) { if (resultCode == RESULT_OK) loadConfigEditor(); return }
        if (requestCode == RULE_FILE_REQUEST_CODE && resultCode == RESULT_OK) {
            val ruleRow = pendingRuleFileRow
            val hostRow = pendingHostFileRow
            val uri = data?.data
            pendingRuleFileRow = null
            pendingHostFileRow = null
            if (uri != null && (ruleRow != null || hostRow != null)) {
                try {
                    if (ruleRow != null) {
                        ruleRow.path.setText(TcpRouteConfigStore.importRuleSource(this, uri))
                    } else if (hostRow != null) {
                        hostRow.path.setText(TcpRouteConfigStore.importHostSource(this, uri))
                    }
                    showStatus(R.string.status_rule_imported_title, R.string.status_rule_imported_message, R.color.teal)
                } catch (_: Exception) {
                    showStatus(R.string.status_check_profile_title, R.string.status_rule_import_failed, R.color.danger)
                }
            }
        } else if (requestCode == CONFIG_IMPORT_REQUEST_CODE && resultCode == RESULT_OK) {
            val uri = data?.data ?: return
            runCatching { TcpRouteConfigStore.readImportedConfig(this, uri) }
                .onSuccess(::applyImportedConfig)
                .onFailure { showStatus(R.string.status_check_profile_title, R.string.status_config_import_failed, R.color.danger) }
        } else if (requestCode == CONFIG_EXPORT_REQUEST_CODE && resultCode == RESULT_OK) {
            val uri = data?.data ?: return
            runCatching { TcpRouteConfigStore.writeExportedConfig(this, uri, TcpRouteConfigStore.loadRawConfig(this)) }
                .onSuccess { showStatus(R.string.status_config_exported_title, R.string.status_config_exported_message, R.color.teal) }
                .onFailure { showStatus(R.string.status_check_profile_title, R.string.status_config_export_failed, R.color.danger) }
        } else if (requestCode == RULE_FILE_REQUEST_CODE) {
            pendingRuleFileRow = null
            pendingHostFileRow = null
        } else if (requestCode == VPN_REQUEST_CODE && resultCode == RESULT_OK) {
            startVpn()
        } else if (requestCode == VPN_REQUEST_CODE) {
            showStatus(R.string.status_permission_title, R.string.status_permission_message, R.color.danger)
        }
    }

    private fun bindViews() {
        pageRoot = findViewById(R.id.pageRoot)
        status = findViewById(R.id.status)
        statusEyebrow = findViewById(R.id.statusEyebrow)
        statusDot = findViewById(R.id.statusDot)
        endpointChip = findViewById(R.id.endpointChip)
        topMenuButton = findViewById(R.id.topMenuButton)
        lanMode = findViewById(R.id.lanMode)
        systemProxy = findViewById(R.id.systemProxy)
        systemProxy.isChecked = SystemProxySettings.enabled(this)
        allowStun = findViewById(R.id.allowStun)
        allowQuic = findViewById(R.id.allowQuic)
        enableIpv6 = findViewById(R.id.enableIpv6)
        upstreamsContainer = findViewById(R.id.upstreamsContainer)
        ruleSetsContainer = findViewById(R.id.ruleSetsContainer)
        routingUpstreamSpinner = findViewById(R.id.routingUpstreamSpinner)
        routingRulesEditor = findViewById(R.id.routingRulesEditor)
        hostsEditor = findViewById(R.id.hostsEditor)
        hostsContainer = findViewById(R.id.hostsContainer)
        vpnToggleButton = findViewById(R.id.vpnToggleButton)
        socksToggleButton = findViewById(R.id.socksToggleButton)
        requestLogButton = findViewById(R.id.requestLogButton)
        routingSummary = findViewById(R.id.routingSummary)
        routingModeGroup = findViewById(R.id.routingModeGroup)
        selectAppsButton = findViewById(R.id.selectAppsButton)
    }

    private fun configureSystemBars() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false)
            applySystemBarInsets(expandedLayoutRoot ?: pageRoot)
        }
    }

    private fun observeWindowWidth() {
        mainContentRoot.addOnLayoutChangeListener { _, left, _, right, _, oldLeft, _, oldRight, _ ->
            val width = right - left
            if (width <= 0 || width == oldRight - oldLeft) return@addOnLayoutChangeListener
            updateResponsiveLayout(width / resources.displayMetrics.density >= EXPANDED_LAYOUT_MIN_WIDTH_DP)
        }
    }

    private fun updateResponsiveLayout(useExpandedLayout: Boolean) {
        if (useExpandedLayout) enterExpandedLayout() else exitExpandedLayout()
    }

    private fun enterExpandedLayout() {
        if (expandedLayoutRoot != null) return

        val parent = pageRoot.parent as? ViewGroup ?: return
        val childIndex = parent.indexOfChild(pageRoot)
        clearSystemBarInsets()
        parent.removeView(pageRoot)

        val expandedRoot = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(getColor(R.color.page))
        }
        expandedRoot.addView(
            pageRoot,
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1.15f)
        )

        val logPanel = layoutInflater.inflate(R.layout.view_request_log_panel, expandedRoot, false)
        expandedRoot.addView(
            logPanel,
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 0.85f).apply {
                setMargins(0, dp(18), dp(20), dp(18))
            }
        )
        parent.addView(
            expandedRoot,
            childIndex,
            ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
        )

        expandedLayoutRoot = expandedRoot
        requestLogButton.visibility = View.GONE
        findViewById<View>(R.id.requestLogSpacer).visibility = View.GONE
        embeddedLogPresenter = RequestLogPresenter(
            this,
            logPanel.findViewById(R.id.embeddedLogScroll),
            logPanel.findViewById(R.id.embeddedLogText),
            logPanel.findViewById(R.id.embeddedLogStatus)
        )
        logPanel.findViewById<View>(R.id.embeddedClearLogButton).setOnClickListener {
            embeddedLogPresenter?.let { presenter -> showClearRequestLogDialog(this, presenter) }
        }
        if (activityStarted) embeddedLogPresenter?.start()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) applySystemBarInsets(expandedRoot)
    }

    private fun exitExpandedLayout() {
        val expandedRoot = expandedLayoutRoot ?: return
        val parent = expandedRoot.parent as? ViewGroup ?: return
        val childIndex = parent.indexOfChild(expandedRoot)

        embeddedLogPresenter?.destroy()
        embeddedLogPresenter = null
        clearSystemBarInsets()
        expandedRoot.removeView(pageRoot)
        parent.removeView(expandedRoot)
        parent.addView(
            pageRoot,
            childIndex,
            ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
        )

        expandedLayoutRoot = null
        requestLogButton.visibility = View.VISIBLE
        findViewById<View>(R.id.requestLogSpacer).visibility = View.VISIBLE
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) applySystemBarInsets(pageRoot)
    }

    @android.annotation.TargetApi(Build.VERSION_CODES.R)
    private fun applySystemBarInsets(root: View) {
        clearSystemBarInsets()
        systemBarsTarget = root
        root.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        root.requestApplyInsets()
    }

    private fun clearSystemBarInsets() {
        systemBarsTarget?.setOnApplyWindowInsetsListener(null)
        systemBarsTarget?.setPadding(0, 0, 0, 0)
        systemBarsTarget = null
    }

    private fun bindActions() {
        topMenuButton.setOnClickListener { showTopMenu() }

        findViewById<Button>(R.id.addProxyButton).setOnClickListener {
            showProxyTypeDialog()
        }
        findViewById<Button>(R.id.addHostButton).setOnClickListener {
            showHostSourceTypeDialog()
        }
        findViewById<Button>(R.id.addWhitelistRuleButton).setOnClickListener {
            selectedRoutingUpstream?.let { showRuleSourceTypeDialog(it, LIST_WHITELIST) }
        }
        findViewById<Button>(R.id.addBlacklistRuleButton).setOnClickListener {
            selectedRoutingUpstream?.let { showRuleSourceTypeDialog(it, LIST_BLACKLIST) }
        }
        routingUpstreamSpinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                if (refreshingRuleUpstreams) return
                selectedRoutingUpstream = upstreamRows.getOrNull(position)
                renderSelectedUpstreamRules()
            }
            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
        }
        listOf(
            R.id.saveConfigButton,
            R.id.saveRoutingConfigButton,
            R.id.saveHostsConfigButton
        ).forEach { buttonId ->
            findViewById<Button>(buttonId).setOnClickListener { saveVisualConfig(showConfirmation = true) }
        }

        vpnToggleButton.setOnClickListener { toggleVpn() }
        socksToggleButton.setOnClickListener { toggleSocks() }
        requestLogButton.setOnClickListener {
            startActivity(Intent(this, RequestLogActivity::class.java))
        }
        selectAppsButton.setOnClickListener { showAppSelectionDialog() }
        systemProxy.setOnCheckedChangeListener { _, enabled -> SystemProxySettings.setEnabled(this, enabled) }
        listOf(allowStun, allowQuic, enableIpv6).forEach { checkbox ->
            checkbox.setOnCheckedChangeListener { _, _ -> saveVpnFeatureControls() }
        }
    }

    private fun showStatus(eyebrow: String, message: String, colorResource: Int) {
        statusEyebrow.text = eyebrow
        statusEyebrow.setTextColor(getColor(colorResource))
        statusDot.backgroundTintList = ColorStateList.valueOf(getColor(colorResource))
        status.text = message
    }

    private fun showStatus(eyebrowResource: Int, messageResource: Int, colorResource: Int) {
        showStatus(getString(eyebrowResource), getString(messageResource), colorResource)
    }

    private fun renderVpnStatus(state: String?) {
        vpnState = state
        when (state) {
            TcpRouteVpnService.STATE_STARTING ->
                showStatus(R.string.status_vpn_starting_title, R.string.status_vpn_starting_message, R.color.teal)
            TcpRouteVpnService.STATE_RUNNING ->
                showStatus(R.string.status_vpn_running_title, R.string.status_vpn_running_message, R.color.teal)
            TcpRouteVpnService.STATE_STOPPED ->
                showStatus(R.string.status_vpn_stopped_title, R.string.status_vpn_stopped_message, R.color.ink_faint)
            TcpRouteVpnService.STATE_ERROR ->
                showStatus(R.string.status_vpn_failed_title, R.string.status_vpn_failed_message, R.color.danger)
        }
        renderConnectionButtons()
    }

    private fun renderSocksStatus(state: String?) {
        socksState = state
        when (state) {
            TcpRouteService.STATE_STARTING ->
                showStatus(R.string.status_socks_starting_title, R.string.status_socks_starting_local, R.color.teal)
            TcpRouteService.STATE_RUNNING ->
                showStatus(R.string.status_socks_running_title, R.string.status_socks_running_message, R.color.teal)
            TcpRouteService.STATE_STOPPED ->
                showStatus(R.string.status_socks_stopped_title, R.string.status_socks_stopped_message, R.color.ink_faint)
            TcpRouteService.STATE_ERROR ->
                showStatus(R.string.status_socks_failed_title, R.string.status_socks_failed_message, R.color.danger)
        }
        renderConnectionButtons()
    }

    private fun renderConnectionButtons() {
        val vpnActive = vpnState == TcpRouteVpnService.STATE_STARTING || vpnState == TcpRouteVpnService.STATE_RUNNING
        val socksActive = socksState == TcpRouteService.STATE_STARTING || socksState == TcpRouteService.STATE_RUNNING
        vpnToggleButton.setText(if (vpnActive) R.string.stop_vpn else R.string.start_vpn)
        socksToggleButton.setText(if (socksActive) R.string.stop_socks else R.string.start_socks)
        vpnToggleButton.alpha = if (vpnState == TcpRouteVpnService.STATE_STARTING) 0.72f else 1f
        socksToggleButton.alpha = if (socksState == TcpRouteService.STATE_STARTING) 0.72f else 1f
        listOf(allowStun, allowQuic, enableIpv6).forEach { it.isEnabled = !vpnActive && !socksActive }
        systemProxy.isEnabled = !vpnActive && !socksActive
        renderAppRoutingAvailability()
    }

    private fun toggleVpn() {
        if (vpnState == TcpRouteVpnService.STATE_STARTING || vpnState == TcpRouteVpnService.STATE_RUNNING) {
            startService(Intent(this, TcpRouteVpnService::class.java).setAction(TcpRouteVpnService.ACTION_STOP))
        } else {
            requestVpnAndStart()
        }
    }

    private fun toggleSocks() {
        if (socksState == TcpRouteService.STATE_STARTING || socksState == TcpRouteService.STATE_RUNNING) {
            startService(Intent(this, TcpRouteService::class.java).setAction(TcpRouteService.ACTION_STOP))
            return
        }
        if (vpnState == TcpRouteVpnService.STATE_STARTING || vpnState == TcpRouteVpnService.STATE_RUNNING) {
            startService(Intent(this, TcpRouteVpnService::class.java).setAction(TcpRouteVpnService.ACTION_STOP))
        }
        requestNotificationPermission()
        val intent = Intent(this, TcpRouteService::class.java)
            .setAction(TcpRouteService.ACTION_START)
            .putExtra(TcpRouteService.EXTRA_ALLOW_LAN, lanMode.isChecked)
            .putExtra(TcpRouteService.EXTRA_ALLOW_STUN, allowStun.isChecked)
            .putExtra(TcpRouteService.EXTRA_ALLOW_QUIC, allowQuic.isChecked)
            .putExtra(TcpRouteService.EXTRA_ENABLE_IPV6, enableIpv6.isChecked)
        startForeground(intent)
        val port = quickListenPort
        val host = if (lanMode.isChecked) "0.0.0.0" else "127.0.0.1"
        endpointChip.text = getString(R.string.endpoint_format, host, port)
        showStatus(
            R.string.status_socks_starting_title,
            if (lanMode.isChecked) R.string.status_socks_starting_lan else R.string.status_socks_starting_local,
            R.color.teal
        )
    }

    private fun showTopMenu() {
        val choice = LanguageManager.selection(this)
        val label = getString(LanguageManager.selectionLabelResource(choice))
        PopupMenu(this, topMenuButton).apply {
            menuInflater.inflate(R.menu.main_overflow, menu)
            menu.findItem(R.id.menuLanguage).title = getString(R.string.language_button, label)
            setOnMenuItemClickListener { item ->
                when (item.itemId) {
                    R.id.menuAdvancedToml -> showAdvancedTomlDialog()
                    R.id.menuImportFile -> chooseConfigFile()
                    R.id.menuImportOnline -> showOnlineConfigDialog()
                    R.id.menuExport -> exportConfig()
                    R.id.menuGeoData -> showGeoDataDialog()
                    R.id.menuSubscriptions -> showSubscriptionDialog()
                    R.id.menuWifiProxy -> showWifiProxyDialog()
                    R.id.menuLanguage -> showLanguageDialog()
                    else -> return@setOnMenuItemClickListener false
                }
                true
            }
            show()
        }
    }

    private fun showSubscriptionDialog() {
        fun open() { startActivityForResult(Intent(this, SubscriptionActivity::class.java), 4089) }
        if (runCatching { collectQuickConfig() != TcpRouteConfigStore.loadEditableConfig(this) }.getOrDefault(true)) {
            AlertDialog.Builder(this).setMessage(R.string.subscription_unsaved)
                .setNegativeButton(android.R.string.cancel, null).setPositiveButton(android.R.string.ok) { _, _ -> loadConfigEditor(); open() }.show()
        } else open()
    }

    private fun showWifiProxyDialog() {
        val config = TcpRouteConfigStore.ensureDefaultConfig(this)
        val port = TcpRouteConfigStore.readListenPort(config)
        AlertDialog.Builder(this)
            .setTitle(R.string.wifi_proxy)
            .setMessage(getString(R.string.wifi_proxy_endpoint, "127.0.0.1", port))
            .setNeutralButton(R.string.copy_proxy_host) { _, _ ->
                val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
                clipboard.setPrimaryClip(android.content.ClipData.newPlainText("Proxy host", "127.0.0.1"))
            }
            .setPositiveButton(R.string.open_wifi_settings) { _, _ ->
                try {
                    startActivity(Intent(android.provider.Settings.ACTION_WIFI_SETTINGS))
                } catch (_: android.content.ActivityNotFoundException) {
                    startActivity(Intent(android.provider.Settings.ACTION_SETTINGS))
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun showGeoDataDialog() {
        val snapshot = GeoDataManager.snapshot(this)
        val lastUpdate = if (snapshot.lastSuccess > 0) {
            DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(snapshot.lastSuccess))
        } else {
            getString(R.string.geodata_never_updated)
        }
        val statusText = getString(
            R.string.geodata_status_format,
            Formatter.formatFileSize(this, snapshot.geositeBytes),
            Formatter.formatFileSize(this, snapshot.geoipBytes),
            lastUpdate,
            snapshot.lastError ?: getString(R.string.geodata_no_error)
        )
        val intervals = longArrayOf(
            GeoDataManager.INTERVAL_OFF,
            GeoDataManager.INTERVAL_DAILY,
            GeoDataManager.INTERVAL_WEEKLY
        )
        val selected = intervals.indexOf(snapshot.interval).coerceAtLeast(0)
        val choices = arrayOf(
            getString(R.string.geodata_auto_off),
            getString(R.string.geodata_auto_daily),
            getString(R.string.geodata_auto_weekly)
        )
        val radioGroup = RadioGroup(this).apply {
            orientation = RadioGroup.VERTICAL
            choices.forEachIndexed { index, choice ->
                addView(RadioButton(this@MainActivity).apply {
                    id = View.generateViewId()
                    text = choice
                    textSize = 16f
                    isChecked = index == selected
                    setPadding(0, dp(4), 0, dp(4))
                    setOnClickListener { GeoDataManager.setInterval(this@MainActivity, intervals[index]) }
                }, matchWidth())
            }
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), 0, dp(24), 0)
            addView(TextView(this@MainActivity).apply {
                text = statusText
                textSize = 16f
                setTextColor(getColor(R.color.ink))
            }, matchWidth())
            addView(radioGroup, matchWidth(topMargin = 8))
        }
        AlertDialog.Builder(this)
            .setTitle(R.string.geodata_manager)
            .setView(content)
            .setPositiveButton(R.string.geodata_update_now) { _, _ ->
                showStatus(R.string.geodata_updating_title, R.string.geodata_updating_message, R.color.teal)
                GeoDataManager.update(applicationContext) { result ->
                    runOnUiThread {
                        if (result.isSuccess) {
                            showStatus(R.string.geodata_updated_title, R.string.geodata_updated_message, R.color.teal)
                        } else {
                            showStatus(R.string.geodata_failed_title, R.string.geodata_failed_message, R.color.danger)
                        }
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun showAdvancedTomlDialog() {
        val editor = EditText(this, null, 0, R.style.FormInput).apply {
            setText(TcpRouteConfigStore.loadRawConfig(this@MainActivity))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or
                InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            gravity = Gravity.TOP or Gravity.START
            typeface = android.graphics.Typeface.MONOSPACE
            textSize = 12f
            minLines = 18
            maxLines = 18
            isVerticalScrollBarEnabled = true
            isHorizontalScrollBarEnabled = true
            setHorizontallyScrolling(true)
        }
        val wrapper = LinearLayout(this).apply {
            setPadding(dp(20), 0, dp(20), 0)
            addView(editor, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(460)))
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle(R.string.advanced_toml)
            .setView(wrapper)
            .setNegativeButton(android.R.string.cancel, null)
            .setPositiveButton(R.string.save_profile, null)
            .create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                if (saveRawConfig(editor.text.toString())) dialog.dismiss()
            }
        }
        dialog.show()
    }

    private fun showLanguageDialog() {
        val choices = LanguageManager.choices
        val labels = arrayOf(
            getString(R.string.language_system),
            getString(R.string.language_english),
            getString(R.string.language_simplified_chinese),
            getString(R.string.language_traditional_chinese)
        )
        val selectedIndex = choices.indexOf(LanguageManager.selection(this)).coerceAtLeast(0)
        AlertDialog.Builder(this)
            .setTitle(R.string.language_dialog_title)
            .setSingleChoiceItems(labels, selectedIndex) { dialog, index ->
                val choice = choices[index]
                if (choice != LanguageManager.selection(this)) {
                    LanguageManager.save(this, choice)
                    dialog.dismiss()
                    recreate()
                } else {
                    dialog.dismiss()
                }
            }
            .show()
    }

    private fun requestVpnAndStart() {
        if (routingPolicy.mode == AppRoutingMode.ALLOWLIST && routingPolicy.packages.isEmpty()) {
            showStatus(R.string.status_select_apps_title, R.string.status_select_apps_message, R.color.danger)
            return
        }

        val intent = VpnService.prepare(this)
        if (intent != null) {
            startActivityForResult(intent, VPN_REQUEST_CODE)
        } else {
            startVpn()
        }
    }

    private fun startVpn() {
        if (systemProxy.isChecked && Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            AlertDialog.Builder(this).setMessage(R.string.system_proxy_vpn_unsupported)
                .setPositiveButton(android.R.string.ok, null).show()
            return
        }
        if (socksState == TcpRouteService.STATE_STARTING || socksState == TcpRouteService.STATE_RUNNING) {
            startService(Intent(this, TcpRouteService::class.java).setAction(TcpRouteService.ACTION_STOP))
        }
        requestNotificationPermission()
        val intent = Intent(this, TcpRouteVpnService::class.java)
            .setAction(TcpRouteVpnService.ACTION_START)
            .putExtra(TcpRouteVpnService.EXTRA_ALLOW_STUN, allowStun.isChecked)
            .putExtra(TcpRouteVpnService.EXTRA_ALLOW_QUIC, allowQuic.isChecked)
            .putExtra(TcpRouteVpnService.EXTRA_ENABLE_IPV6, enableIpv6.isChecked)
        startForeground(intent)
        endpointChip.text = getString(R.string.vpn_endpoint)
        showStatus(R.string.status_vpn_starting_title, R.string.status_vpn_starting_message, R.color.teal)
    }

    private fun loadVpnFeatureControls() {
        val policy = VpnFeaturePolicyStore.load(this)
        allowStun.isChecked = policy.allowStun
        allowQuic.isChecked = policy.allowQuic
        enableIpv6.isChecked = policy.enableIpv6
    }

    private fun saveVpnFeatureControls() {
        VpnFeaturePolicyStore.save(
            this,
            VpnFeaturePolicy(allowStun.isChecked, allowQuic.isChecked, enableIpv6.isChecked)
        )
    }

    private fun startForeground(intent: Intent) {
        startForegroundService(intent)
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), NOTIFICATION_REQUEST_CODE)
        }
    }

    private fun loadConfigEditor() {
        val config = TcpRouteConfigStore.loadEditableConfig(this)
        renderQuickConfig(config)
        endpointChip.text = getString(R.string.endpoint_format, "127.0.0.1", config.listenPort)
    }

    private fun renderQuickConfig(config: EditableTcpRouteConfig) {
        quickDnsToml = config.dnsToml
        quickListenPort = config.listenPort
        ruleRowsByUpstream.clear()
        ruleSetsContainer.removeAllViews()
        upstreamRows.clear()
        upstreamsContainer.removeAllViews()
        selectedRoutingUpstream = null
        hostRows.clear()
        hostsContainer.removeAllViews()
        val configuredUpstreams = if (config.upstreams.none { it.proxyUrl.trim().startsWith("direct://", true) }) {
            listOf(EditableUpstream.direct()) + config.upstreams
        } else {
            config.upstreams
        }
        configuredUpstreams.forEach { upstream ->
            addUpstreamRow(upstream)
            val owner = upstreamRows.last()
            upstream.whitelist.forEach { addRuleSetRow(owner, LIST_WHITELIST, it) }
            upstream.blacklist.forEach { addRuleSetRow(owner, LIST_BLACKLIST, it) }
        }
        if (upstreamRows.isEmpty()) addUpstreamRow(EditableUpstream.direct())
        config.hosts.forEach(::addHostRow)
        refreshRuleUpstreamOptions()
    }

    private fun showProxyTypeDialog() {
        AlertDialog.Builder(this)
            .setTitle(R.string.choose_proxy_type)
            .setItems(CREATABLE_PROXY_SCHEMES.map { it.uppercase(Locale.ROOT) }.toTypedArray()) { _, which ->
                addUpstreamRow(EditableUpstream.proxyTemplate(CREATABLE_PROXY_SCHEMES[which]))
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun showHostSourceTypeDialog() {
        val sourceTypes = listOf(SOURCE_LOCAL, SOURCE_REMOTE)
        AlertDialog.Builder(this)
            .setTitle(R.string.choose_host_source_type)
            .setItems(arrayOf(getString(R.string.rule_type_local), getString(R.string.rule_type_remote))) { _, which ->
                addHostRow(EditableHostSource.template(sourceTypes[which]))
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun showRuleSourceTypeDialog(owner: UpstreamRow, listKind: String) {
        AlertDialog.Builder(this)
            .setTitle(R.string.choose_rule_source_type)
            .setItems(
                arrayOf(
                    getString(R.string.rule_type_local),
                    getString(R.string.rule_type_remote),
                    getString(R.string.rule_type_geosite),
                    getString(R.string.rule_type_geoip)
                )
            ) { _, which ->
                addRuleSetRow(owner, listKind, EditableDomainList.template(SOURCE_TYPES[which]))
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun chooseConfigFile() {
        startActivityForResult(
            Intent(Intent.ACTION_OPEN_DOCUMENT)
                .addCategory(Intent.CATEGORY_OPENABLE)
                .setType("*/*"),
            CONFIG_IMPORT_REQUEST_CODE
        )
    }

    private fun exportConfig() {
        startActivityForResult(
            Intent(Intent.ACTION_CREATE_DOCUMENT)
                .addCategory(Intent.CATEGORY_OPENABLE)
                .setType("application/toml")
                .putExtra(Intent.EXTRA_TITLE, "tcproute.toml"),
            CONFIG_EXPORT_REQUEST_CODE
        )
    }

    private fun showOnlineConfigDialog() {
        val input = formInput("", getString(R.string.online_config_url_hint)).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        }
        val wrapper = LinearLayout(this).apply {
            setPadding(dp(20), dp(4), dp(20), 0)
            addView(input, matchWidth())
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle(R.string.import_online_config)
            .setView(wrapper)
            .setNegativeButton(android.R.string.cancel, null)
            .setPositiveButton(R.string.import_config, null)
            .create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val source = input.text.toString().trim()
                if (!source.startsWith("https://") && !source.startsWith("http://")) {
                    input.error = getString(R.string.online_config_url_error)
                    return@setOnClickListener
                }
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).isEnabled = false
                Thread {
                    val result = runCatching { TcpRouteConfigStore.downloadConfig(source) }
                    runOnUiThread {
                        result.onSuccess {
                            dialog.dismiss()
                            applyImportedConfig(it)
                        }.onFailure {
                            dialog.getButton(AlertDialog.BUTTON_POSITIVE).isEnabled = true
                            input.error = getString(R.string.status_config_import_failed)
                        }
                    }
                }.start()
            }
        }
        dialog.show()
    }

    private fun applyImportedConfig(content: String) {
        try {
            val config = TcpRouteConfigStore.parseEditableConfig(content)
            TcpRouteConfigStore.saveRawConfig(this, content)
            renderQuickConfig(config)
            endpointChip.text = getString(R.string.endpoint_format, "127.0.0.1", config.listenPort)
            showStatus(R.string.status_config_imported_title, R.string.status_config_imported_message, R.color.teal)
        } catch (_: Exception) {
            showStatus(R.string.status_check_profile_title, R.string.status_config_import_failed, R.color.danger)
        }
    }

    private fun addUpstreamRow(upstream: EditableUpstream) {
        val isDirect = upstream.proxyUrl.trim().startsWith("direct://", ignoreCase = true)
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(14), dp(14), dp(14))
            setBackgroundResource(R.drawable.bg_upstream)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = dp(14) }
        }
        val title = TextView(this).apply {
            setTextColor(getColor(R.color.ink))
            textSize = 13f
            typeface = android.graphics.Typeface.DEFAULT_BOLD
            letterSpacing = 0.08f
        }
        val name = formInput(upstream.name, getString(R.string.route_name_hint))
        val alias = formInput(upstream.alias, getString(R.string.field_alias))
        val proxyUrl = formInput(upstream.proxyUrl, getString(R.string.proxy_url_hint)).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        }
        val proxyValidation = TextView(this).apply {
            textSize = 12f
            layoutParams = matchWidth(topMargin = 5)
        }
        val dnsResolve = CheckBox(this).apply {
            text = getString(R.string.resolve_dns_locally)
            isChecked = upstream.dnsResolve
            setTextColor(getColor(R.color.ink_muted))
            textSize = 13f
            buttonTintList = ColorStateList.valueOf(getColor(R.color.teal))
        }
        val credit = numericEditText(upstream.credit)
        val sleep = numericEditText(upstream.sleep)
        val correctDelay = numericEditText(upstream.correctDelay)
        val row = UpstreamRow(container, title, name, proxyUrl, proxyValidation, dnsResolve, credit, sleep, correctDelay, upstream.dialProxyFor, upstream.remove, alias)

        if (isDirect) {
            upstreamRows.add(row)
            ruleRowsByUpstream.putIfAbsent(row, mutableListOf())
            refreshRuleUpstreamOptions()
            return
        }

        watchUpstreamNameChanges(name)
        watchUpstreamNameChanges(alias)
        watchProxyUrlChanges(row)

        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(title, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(Button(this@MainActivity, null, 0, R.style.DangerButton).apply {
                text = getString(R.string.remove)
                setOnClickListener {
                    ruleRowsByUpstream.remove(row)
                    upstreamRows.remove(row)
                    upstreamsContainer.removeView(container)
                    if (selectedRoutingUpstream == row) selectedRoutingUpstream = upstreamRows.firstOrNull()
                    refreshUpstreamTitles()
                    refreshRuleUpstreamOptions()
                }
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(40)))
        }

        container.addView(header)
        container.addView(formLabel(getString(R.string.field_name), topMargin = 12))
        container.addView(name, matchWidth(topMargin = 6))
        container.addView(formLabel(getString(R.string.field_alias), topMargin = 12))
        container.addView(alias, matchWidth(topMargin = 6))
        container.addView(formLabel(getString(R.string.field_proxy_url), topMargin = 12))
        container.addView(proxyUrl, matchWidth(topMargin = 6))
        container.addView(proxyValidation)
        renderProxyUrlValidation(row)
        container.addView(dnsResolve, matchWidth(topMargin = 8))

        val tuning = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            isBaselineAligned = false
            addView(numericColumn(getString(R.string.field_credit), credit), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(space(dp(8)))
            addView(numericColumn(getString(R.string.field_sleep_ms), sleep), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(space(dp(8)))
            addView(numericColumn(getString(R.string.field_delay_ms), correctDelay), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }
        container.addView(tuning, matchWidth(topMargin = 6))

        upstreamRows.add(row)
        ruleRowsByUpstream.putIfAbsent(row, mutableListOf())
        upstreamsContainer.addView(container)
        refreshUpstreamTitles()
        refreshRuleUpstreamOptions()
    }

    private fun addHostRow(entry: EditableHostSource) {
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(14), dp(14), dp(14))
            setBackgroundResource(R.drawable.bg_upstream)
            layoutParams = matchWidth(topMargin = 14)
        }
        val title = TextView(this).apply {
            setTextColor(getColor(R.color.ink))
            textSize = 13f
            typeface = android.graphics.Typeface.DEFAULT_BOLD
            letterSpacing = 0.08f
        }
        val sourceType = formSpinner().apply {
            adapter = spinnerAdapter(listOf(getString(R.string.rule_type_local), getString(R.string.rule_type_remote)))
            setSelection(if (entry.sourceType == SOURCE_REMOTE) 1 else 0, false)
        }
        val matchType = formSpinner().apply {
            adapter = spinnerAdapter(matchTypeLabels())
            setSelection(MATCH_TYPES.indexOf(entry.type.lowercase()).coerceAtLeast(0), false)
        }
        val path = formInput(entry.path, getString(R.string.host_source_hint)).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        }
        val updateInterval = formInput(entry.updateInterval, getString(R.string.update_interval_hint))
        val credit = numericEditText(entry.credit).apply {
            gravity = Gravity.START or Gravity.CENTER_VERTICAL
        }
        val updateIntervalLabel = formLabel(getString(R.string.field_update_interval), topMargin = 12)
        val chooseFile = Button(this, null, 0, R.style.SecondaryButton).apply {
            text = getString(R.string.choose_host_file)
        }
        val row = HostSourceRow(
            container,
            title,
            sourceType,
            matchType,
            path,
            updateInterval,
            updateIntervalLabel,
            credit,
            chooseFile
        )
        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(title, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(Button(this@MainActivity, null, 0, R.style.DangerButton).apply {
                text = getString(R.string.remove)
                setOnClickListener {
                    hostRows.remove(row)
                    hostsContainer.removeView(container)
                    refreshHostTitles()
                }
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(40)))
        }
        container.addView(header)
        container.addView(formLabel(getString(R.string.field_source_type), topMargin = 12))
        container.addView(sourceType, matchWidth(topMargin = 6))
        container.addView(formLabel(getString(R.string.field_match_type), topMargin = 12))
        container.addView(matchType, matchWidth(topMargin = 6))
        container.addView(formLabel(getString(R.string.field_credit), topMargin = 12))
        container.addView(credit, matchWidth(topMargin = 6))
        container.addView(formLabel(getString(R.string.field_source), topMargin = 12))
        container.addView(path, matchWidth(topMargin = 6))
        container.addView(chooseFile, matchWidth(topMargin = 8))
        container.addView(updateIntervalLabel)
        container.addView(updateInterval, matchWidth(topMargin = 6))
        chooseFile.setOnClickListener { chooseHostFile(row) }
        sourceType.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                renderHostSourceType(row)
            }
            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
        }
        hostRows.add(row)
        hostsContainer.addView(container)
        refreshHostTitles()
        renderHostSourceType(row)
    }

    private fun refreshHostTitles() {
        hostRows.forEachIndexed { index, row -> row.title.text = getString(R.string.host_number, index + 1) }
    }

    private fun renderHostSourceType(row: HostSourceRow) {
        val remote = row.sourceType.selectedItemPosition == 1
        row.chooseFile.visibility = if (remote) View.GONE else View.VISIBLE
        row.updateIntervalLabel.visibility = if (remote) View.VISIBLE else View.GONE
        row.updateInterval.visibility = if (remote) View.VISIBLE else View.GONE
    }

    private fun chooseHostFile(row: HostSourceRow) {
        pendingRuleFileRow = null
        pendingHostFileRow = row
        startActivityForResult(
            Intent(Intent.ACTION_OPEN_DOCUMENT)
                .addCategory(Intent.CATEGORY_OPENABLE)
                .setType("text/*"),
            RULE_FILE_REQUEST_CODE
        )
    }

    private fun addRuleSetRow(owner: UpstreamRow, listKind: String, ruleSet: EditableDomainList) {
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(14), dp(14), dp(14))
            setBackgroundResource(R.drawable.bg_upstream)
            layoutParams = matchWidth(topMargin = 14)
        }
        val title = TextView(this).apply {
            setTextColor(getColor(R.color.ink))
            textSize = 13f
            typeface = android.graphics.Typeface.DEFAULT_BOLD
            letterSpacing = 0.08f
        }
        val sourceType = formSpinner().apply {
            adapter = spinnerAdapter(
                listOf(
                    getString(R.string.rule_type_local),
                    getString(R.string.rule_type_remote),
                    getString(R.string.rule_type_geosite),
                    getString(R.string.rule_type_geoip)
                )
            )
            setSelection(SOURCE_TYPES.indexOf(ruleSet.sourceType).coerceAtLeast(0), false)
        }
        val matchType = formSpinner().apply {
            adapter = spinnerAdapter(matchTypeLabels())
            setSelection(MATCH_TYPES.indexOf(ruleSet.type.lowercase()).coerceAtLeast(0), false)
        }
        val isGeoIP = ruleSet.sourceType == SOURCE_GEOIP
        val geositeType = formInput(
            if (ruleSet.sourceType == SOURCE_GEOSITE || isGeoIP) ruleSet.type.substringAfter(':')
            else "gfw",
            getString(if (isGeoIP) R.string.geoip_rule_hint else R.string.geosite_rule_hint)
        )
        val path = formInput(ruleSet.path, getString(R.string.rule_source_hint)).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        }
        val updateInterval = formInput(ruleSet.updateInterval, getString(R.string.update_interval_hint))
        val updateIntervalLabel = formLabel(getString(R.string.field_update_interval), topMargin = 12)
        val matchTypeLabel = formLabel(getString(R.string.field_match_type), topMargin = 12)
        val geositeTypeLabel = formLabel(getString(R.string.field_geosite_rule), topMargin = 12)
        val pathLabel = formLabel(getString(R.string.field_source), topMargin = 12)
        val chooseFile = Button(this, null, 0, R.style.SecondaryButton).apply {
            text = getString(R.string.choose_rule_file)
        }
        val row = RuleSetRow(
            container,
            title,
            sourceType,
            matchType,
            geositeType,
            path,
            updateInterval,
            updateIntervalLabel,
            matchTypeLabel,
            geositeTypeLabel,
            pathLabel,
            chooseFile,
            owner,
            listKind
        )

        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(title, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(Button(this@MainActivity, null, 0, R.style.DangerButton).apply {
                text = getString(R.string.remove)
                setOnClickListener {
                    ruleRowsByUpstream[owner]?.remove(row)
                    ruleSetsContainer.removeView(container)
                    refreshRuleTitles()
                }
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(40)))
        }
        container.addView(header)
        container.addView(formLabel(getString(R.string.field_source_type), topMargin = 12))
        container.addView(sourceType, matchWidth(topMargin = 6))
        container.addView(matchTypeLabel)
        container.addView(matchType, matchWidth(topMargin = 6))
        container.addView(geositeTypeLabel)
        container.addView(geositeType, matchWidth(topMargin = 6))
        container.addView(pathLabel)
        container.addView(path, matchWidth(topMargin = 6))
        container.addView(chooseFile, matchWidth(topMargin = 8))
        container.addView(updateIntervalLabel)
        container.addView(updateInterval, matchWidth(topMargin = 6))

        chooseFile.setOnClickListener { chooseRuleFile(row) }
        var previousSourceType = ruleSet.sourceType
        sourceType.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                val selectedSourceType = SOURCE_TYPES[position.coerceIn(SOURCE_TYPES.indices)]
                if (selectedSourceType != previousSourceType) {
                    when (selectedSourceType) {
                        SOURCE_GEOSITE -> row.path.setText("geosite.dat")
                        SOURCE_GEOIP -> row.path.setText("geoip.dat")
                    }
                    previousSourceType = selectedSourceType
                }
                renderRuleSourceType(row)
            }
            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
        }
        path.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(text: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(text: CharSequence?, start: Int, before: Int, count: Int) = Unit
            override fun afterTextChanged(text: Editable?) = renderRuleUpdateInterval(row)
        })

        ruleRowsByUpstream.getOrPut(owner) { mutableListOf() }.add(row)
        if (selectedRoutingUpstream == owner) ruleSetsContainer.addView(container)
        refreshRuleTitles()
        renderRuleSourceType(row)
    }

    private fun renderRuleSourceType(row: RuleSetRow) {
        val sourceType = SOURCE_TYPES[row.sourceType.selectedItemPosition.coerceIn(SOURCE_TYPES.indices)]
        val isGeoSite = sourceType == SOURCE_GEOSITE
        val isGeoIP = sourceType == SOURCE_GEOIP
        val isGeoData = isGeoSite || isGeoIP
        val isRemote = sourceType == SOURCE_REMOTE
        row.matchTypeLabel.visibility = if (isGeoData) View.GONE else View.VISIBLE
        row.matchType.visibility = if (isGeoData) View.GONE else View.VISIBLE
        row.geositeTypeLabel.visibility = if (isGeoData) View.VISIBLE else View.GONE
        row.geositeType.visibility = if (isGeoData) View.VISIBLE else View.GONE
        row.pathLabel.visibility = if (isGeoData) View.GONE else View.VISIBLE
        row.path.visibility = if (isGeoData) View.GONE else View.VISIBLE
        row.chooseFile.visibility = if (isRemote || isGeoData) View.GONE else View.VISIBLE
        row.geositeTypeLabel.text = getString(if (isGeoIP) R.string.field_geoip_rule else R.string.field_geosite_rule)
        row.geositeType.hint = getString(if (isGeoIP) R.string.geoip_rule_hint else R.string.geosite_rule_hint)
        row.pathLabel.text = getString(R.string.field_source)
        row.path.hint = getString(R.string.rule_source_hint)
        if (isGeoData) {
            val currentRules = row.geositeType.text.toString().trim()
            if (currentRules.startsWith("geosite:", ignoreCase = true) ||
                currentRules.startsWith("geoip:", ignoreCase = true)
            ) {
                row.geositeType.setText(currentRules.substringAfter(':'))
            } else if (currentRules.isEmpty() || (isGeoIP && currentRules == "gfw")) {
                row.geositeType.setText(if (isGeoIP) "cn,private" else "gfw")
            }
            val currentPath = row.path.text.toString().trim()
            if (currentPath.isEmpty() || currentPath.startsWith("geosite:", ignoreCase = true) ||
                currentPath.startsWith("geoip:", ignoreCase = true) ||
                (isGeoIP && currentPath == "geosite.dat") || (!isGeoIP && currentPath == "geoip.dat")
            ) {
                row.path.setText(if (isGeoIP) "geoip.dat" else "geosite.dat")
            }
        } else if (row.path.text.toString().trim() in setOf("geosite.dat", "geoip.dat")) {
            row.path.setText("")
        }
        renderRuleUpdateInterval(row)
    }

    private fun renderRuleUpdateInterval(row: RuleSetRow) {
        val sourceType = SOURCE_TYPES[row.sourceType.selectedItemPosition.coerceIn(SOURCE_TYPES.indices)]
        val visible = sourceType == SOURCE_REMOTE
        row.updateIntervalLabel.visibility = if (visible) View.VISIBLE else View.GONE
        row.updateInterval.visibility = if (visible) View.VISIBLE else View.GONE
    }

    private fun refreshRuleTitles() {
        ruleRowsByUpstream.values.flatten().forEach { row ->
            val rowsOfKind = ruleRowsByUpstream[row.owner].orEmpty().filter { it.listKind == row.listKind }
            val index = rowsOfKind.indexOf(row) + 1
            row.title.text = if (row.listKind == LIST_WHITELIST) {
                getString(R.string.whitelist_rule_number, index)
            } else {
                getString(R.string.blacklist_rule_number, index)
            }
        }
    }

    private fun watchUpstreamNameChanges(field: EditText) {
        field.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(text: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(text: CharSequence?, start: Int, before: Int, count: Int) {
                refreshRuleUpstreamOptions()
                refreshUpstreamTitles()
            }
            override fun afterTextChanged(text: Editable?) = Unit
        })
    }

    private fun watchProxyUrlChanges(row: UpstreamRow) {
        row.proxyUrl.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(text: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(text: CharSequence?, start: Int, before: Int, count: Int) {
                row.proxyUrl.error = null
                renderProxyUrlValidation(row)
            }
            override fun afterTextChanged(text: Editable?) = Unit
        })
    }

    private fun renderProxyUrlValidation(row: UpstreamRow): String? {
        val error = TcpRouteConfigStore.proxyUrlError(row.proxyUrl.text.toString())
        row.proxyValidation.text = error?.let(::localizedProxyError) ?: getString(R.string.proxy_url_valid)
        row.proxyValidation.setTextColor(getColor(if (error == null) R.color.teal else R.color.danger))
        return error
    }

    private fun localizedProxyError(error: String): String = when {
        error == "Proxy URL is empty or contains whitespace." -> getString(R.string.proxy_error_empty_or_whitespace)
        error == "Proxy URL is malformed." -> getString(R.string.proxy_error_malformed)
        error == "Proxy URL needs a scheme." -> getString(R.string.proxy_error_scheme)
        error.startsWith("Unsupported proxy type: ") -> getString(
            R.string.proxy_error_unsupported,
            error.removePrefix("Unsupported proxy type: ").removeSuffix(".")
        )
        error == "Direct URL is incomplete." -> getString(R.string.proxy_error_direct_incomplete)
        error == "VMess URL payload is not valid Base64." -> getString(R.string.proxy_error_vmess_base64)
        error == "VMess JSON payload is malformed." -> getString(R.string.proxy_error_vmess_json)
        error == "VMess JSON payload is incomplete." -> getString(R.string.proxy_error_vmess_json_incomplete)
        error == "VMess URL payload is incomplete." -> getString(R.string.proxy_error_vmess_payload_incomplete)
        error == "Shadowsocks URL payload is not valid Base64." -> getString(R.string.proxy_error_shadowsocks_base64)
        error == "Shadowsocks URL payload is incomplete." -> getString(R.string.proxy_error_shadowsocks_payload)
        error == "Encoded proxy URL payload is not valid Base64." -> getString(R.string.proxy_error_encoded_base64)
        error == "Encoded proxy URL payload is incomplete." -> getString(R.string.proxy_error_encoded_payload)
        error == "Proxy URL needs a host." -> getString(R.string.proxy_error_host)
        error == "Proxy URL needs a valid port." -> getString(R.string.proxy_error_port)
        error == "This proxy type does not support a URL path." -> getString(R.string.proxy_error_path)
        error == "Shadowsocks URL needs a method and password." -> getString(R.string.proxy_error_shadowsocks_credentials)
        error == "This proxy type needs credentials before the host." -> getString(R.string.proxy_error_credentials)
        error == "WireGuard URL needs publicKey and address parameters." -> getString(R.string.proxy_error_wireguard_parameters)
        else -> error
    }

    private fun refreshRuleUpstreamOptions() {
        val upstreamNames = upstreamRows.map { it.alias.text.toString().ifBlank { it.name.text.toString().trim() } }
        val selected = selectedRoutingUpstream?.takeIf { it in upstreamRows } ?: upstreamRows.firstOrNull()
        selectedRoutingUpstream = selected
        refreshingRuleUpstreams = true
        try {
            routingUpstreamSpinner.adapter = spinnerAdapter(upstreamNames)
            val index = upstreamRows.indexOf(selected)
            if (index >= 0) routingUpstreamSpinner.setSelection(index, false)
        } finally {
            refreshingRuleUpstreams = false
        }
        renderSelectedUpstreamRules()
    }

    private fun renderSelectedUpstreamRules() {
        ruleSetsContainer.removeAllViews()
        ruleRowsByUpstream[selectedRoutingUpstream].orEmpty().forEach { ruleSetsContainer.addView(it.container) }
        refreshRuleTitles()
    }

    private fun chooseRuleFile(row: RuleSetRow) {
        pendingRuleFileRow = row
        pendingHostFileRow = null
        startActivityForResult(
            Intent(Intent.ACTION_OPEN_DOCUMENT)
                .addCategory(Intent.CATEGORY_OPENABLE)
                .setType("text/*"),
            RULE_FILE_REQUEST_CODE
        )
    }

    private fun refreshUpstreamTitles() {
        var proxyIndex = 0
        upstreamRows.forEach { row ->
            row.title.text = if (row.proxyUrl.text.toString().trim().startsWith("direct://", ignoreCase = true)) {
                getString(R.string.default_upstream)
            } else {
                proxyIndex += 1
                row.alias.text.toString().ifBlank { getString(R.string.upstream_number, proxyIndex) }
            }
        }
    }

    private fun formLabel(label: String, topMargin: Int = 0): TextView =
        TextView(this, null, 0, R.style.FormLabel).apply {
            text = label
            layoutParams = matchWidth(topMargin)
        }

    private fun formInput(value: String, hintText: String): EditText =
        EditText(this, null, 0, R.style.FormInput).apply {
            isEnabled = true
            isClickable = true
            isFocusable = true
            isFocusableInTouchMode = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            hint = hintText
            setText(value)
            setSingleLine(true)
            setSelectAllOnFocus(false)
        }

    private fun formSpinner(): Spinner = Spinner(this).apply {
        setBackgroundResource(R.drawable.bg_input)
        minimumHeight = dp(48)
        setPadding(dp(8), 0, dp(8), 0)
    }

    private fun spinnerAdapter(items: List<String>): ArrayAdapter<String> =
        ArrayAdapter(this, android.R.layout.simple_spinner_item, items).apply {
            setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        }

    private fun matchTypeLabels(): List<String> = listOf(
        getString(R.string.rule_type_base),
        getString(R.string.rule_type_suffix),
        getString(R.string.rule_type_pan),
        getString(R.string.rule_type_regex)
    )

    private fun numericEditText(value: Int): EditText =
        formInput(value.toString(), "0").apply {
            inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_SIGNED
            gravity = Gravity.CENTER
            setPadding(dp(8), dp(8), dp(8), dp(8))
        }

    private fun numericColumn(label: String, field: EditText): LinearLayout =
        LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(formLabel(label))
            addView(field, matchWidth(topMargin = 6))
        }

    private fun collectQuickConfig(): EditableTcpRouteConfig {
        return EditableTcpRouteConfig(
            dnsToml = quickDnsToml,
            listenPort = quickListenPort,
            upstreams = upstreamRows.map { row ->
                val rules = ruleRowsByUpstream[row].orEmpty()
                EditableUpstream(
                    name = row.name.text.toString(),
                    alias = row.alias.text.toString(),
                    proxyUrl = row.proxyUrl.text.toString(),
                    dnsResolve = row.dnsResolve.isChecked,
                    credit = row.credit.text.toString().toIntOrNull() ?: 0,
                    sleep = row.sleep.text.toString().toIntOrNull() ?: 0,
                    correctDelay = row.correctDelay.text.toString().toIntOrNull() ?: 0,
                    dialProxyFor = row.dialProxyFor,
                    remove = row.remove,
                    whitelist = rules.filter { it.listKind == LIST_WHITELIST }.map(::editableDomainList),
                    blacklist = rules.filter { it.listKind == LIST_BLACKLIST }.map(::editableDomainList)
                )
            },
            hosts = hostRows.map {
                EditableHostSource(
                    path = it.path.text.toString(),
                    sourceType = if (it.sourceType.selectedItemPosition == 1) SOURCE_REMOTE else SOURCE_LOCAL,
                    updateInterval = it.updateInterval.text.toString(),
                    type = MATCH_TYPES[it.matchType.selectedItemPosition.coerceIn(MATCH_TYPES.indices)],
                    credit = it.credit.text.toString().toIntOrNull() ?: 0
                )
            }
        )
    }

    private fun editableDomainList(row: RuleSetRow): EditableDomainList {
        val sourceType = SOURCE_TYPES[row.sourceType.selectedItemPosition.coerceIn(SOURCE_TYPES.indices)]
        return EditableDomainList(
            path = row.path.text.toString(),
            sourceType = sourceType,
            type = if (sourceType == SOURCE_GEOSITE || sourceType == SOURCE_GEOIP) {
                row.geositeType.text.toString()
            } else {
                MATCH_TYPES[row.matchType.selectedItemPosition.coerceIn(MATCH_TYPES.indices)]
            },
            updateInterval = row.updateInterval.text.toString()
        )
    }

    private fun saveVisualConfig(showConfirmation: Boolean): Boolean {
        try {
            val invalidProxy = upstreamRows.firstNotNullOfOrNull { row ->
                renderProxyUrlValidation(row)?.let { row to it }
            }
            invalidProxy?.let { (row, error) ->
                val reason = localizedProxyError(error)
                row.proxyUrl.error = reason
                if (showConfirmation) {
                    row.proxyUrl.requestFocus()
                    showStatus(
                        getString(R.string.status_check_profile_title),
                        getString(R.string.status_proxy_save_failed, row.name.text.toString().ifBlank { getString(R.string.field_proxy_url) }, reason),
                        R.color.danger
                    )
                }
                return false
            }
            val config = collectQuickConfig()
            TcpRouteConfigStore.saveEditableConfig(this, config)
            endpointChip.text = getString(R.string.endpoint_format, "127.0.0.1", config.listenPort)
            if (showConfirmation) {
                showStatus(R.string.status_profile_saved_title, R.string.status_profile_saved_message, R.color.teal)
            }
            return true
        } catch (error: Exception) {
            if (showConfirmation) {
                showStatus(
                    getString(R.string.status_check_profile_title),
                    getString(R.string.status_save_failed_reason, error.message ?: getString(R.string.status_save_profile_failed)),
                    R.color.danger
                )
            }
            return false
        }
    }

    private fun saveRawConfig(raw: String): Boolean {
        try {
            val config = TcpRouteConfigStore.parseEditableConfig(raw)
            TcpRouteConfigStore.saveRawConfig(this, raw)
            renderQuickConfig(config)
            endpointChip.text = getString(R.string.endpoint_format, "127.0.0.1", config.listenPort)
            showStatus(R.string.status_profile_saved_title, R.string.status_advanced_saved_message, R.color.teal)
            return true
        } catch (error: Exception) {
            showStatus(
                getString(R.string.status_check_toml_title),
                getString(R.string.status_save_failed_reason, error.message ?: getString(R.string.status_save_toml_failed)),
                R.color.danger
            )
            return false
        }
    }

    private fun loadAppRoutingControls() {
        routingPolicy = AppRoutingPolicyStore.load(this)
        installedApps = InstalledAppStore.loadLaunchableApps(this)
        routingModeGroup.removeAllViews()
        routingModeGroup.addView(routingRadioButton(APP_ROUTING_ALL_ID, R.string.routing_all_title, R.string.routing_all_detail))
        routingModeGroup.addView(routingRadioButton(APP_ROUTING_ALLOWLIST_ID, R.string.routing_selected_title, R.string.routing_selected_detail))
        routingModeGroup.addView(routingRadioButton(APP_ROUTING_BLOCKLIST_ID, R.string.routing_bypass_title, R.string.routing_bypass_detail))

        routingModeGroup.setOnCheckedChangeListener { _, checkedId ->
            val mode = when (checkedId) {
                APP_ROUTING_ALLOWLIST_ID -> AppRoutingMode.ALLOWLIST
                APP_ROUTING_BLOCKLIST_ID -> AppRoutingMode.BLOCKLIST
                else -> AppRoutingMode.ALL
            }
            val packages = if (mode == AppRoutingMode.ALL || mode != routingPolicy.mode) emptySet() else routingPolicy.packages
            routingPolicy = AppRoutingPolicy(mode, packages)
            AppRoutingPolicyStore.save(this, routingPolicy)
            renderAppSelection()
        }

        routingModeGroup.check(
            when (routingPolicy.mode) {
                AppRoutingMode.ALL -> APP_ROUTING_ALL_ID
                AppRoutingMode.ALLOWLIST -> APP_ROUTING_ALLOWLIST_ID
                AppRoutingMode.BLOCKLIST -> APP_ROUTING_BLOCKLIST_ID
            }
        )
        renderAppSelection()
    }

    private fun renderAppRoutingAvailability() {
        if (!::routingModeGroup.isInitialized) return
        val socksActive = socksState == TcpRouteService.STATE_STARTING ||
            socksState == TcpRouteService.STATE_RUNNING
        if (socksActive) {
            routingModeGroup.visibility = View.GONE
            selectAppsButton.visibility = View.GONE
            routingSummary.text = getString(R.string.routing_socks_summary)
        } else {
            routingModeGroup.visibility = View.VISIBLE
            renderAppSelection()
        }
    }

    private fun routingRadioButton(viewId: Int, titleResource: Int, detailResource: Int): RadioButton =
        RadioButton(this, null, 0, R.style.SegmentButton).apply {
            id = viewId
            isSaveEnabled = false
            text = getString(
                R.string.routing_option_format,
                getString(titleResource),
                getString(detailResource)
            )
            gravity = Gravity.START or Gravity.CENTER_VERTICAL
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46))
        }

    private fun renderAppSelection() {
        updateRoutingSummary()
        val showApps = routingPolicy.mode != AppRoutingMode.ALL
        selectAppsButton.visibility = if (showApps) View.VISIBLE else View.GONE
    }

    private fun showAppSelectionDialog() {
        if (routingPolicy.mode == AppRoutingMode.ALL) return

        val selectedPackages = routingPolicy.packages.toMutableSet()
        val searchInput = formInput("", getString(R.string.search_apps))
        val listContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), 0, dp(20), 0)
            addView(searchInput, matchWidth())
            addView(ScrollView(this@MainActivity).apply {
                isFillViewport = true
                addView(listContainer)
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(420)).apply {
                topMargin = dp(10)
            })
        }

        fun renderDialogApps(queryText: String) {
            listContainer.removeAllViews()
            val query = queryText.trim().lowercase()
            val filtered = installedApps.filter {
                query.isEmpty() || it.label.lowercase().contains(query) || it.packageName.lowercase().contains(query)
            }
            if (filtered.isEmpty()) {
                listContainer.addView(TextView(this).apply {
                    text = getString(R.string.no_matching_apps)
                    gravity = Gravity.CENTER
                    setTextColor(getColor(R.color.ink_faint))
                    setPadding(0, dp(24), 0, dp(18))
                })
            } else {
                filtered.forEach { app ->
                    listContainer.addView(appSelectionDialogRow(app, selectedPackages))
                }
            }
        }

        searchInput.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(text: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(text: CharSequence?, start: Int, before: Int, count: Int) {
                renderDialogApps(text?.toString().orEmpty())
            }
            override fun afterTextChanged(text: Editable?) = Unit
        })
        renderDialogApps("")

        AlertDialog.Builder(this)
            .setTitle(R.string.select_apps_dialog_title)
            .setView(content)
            .setPositiveButton(R.string.apply_app_selection) { _, _ ->
                routingPolicy = routingPolicy.copy(packages = selectedPackages)
                AppRoutingPolicyStore.save(this, routingPolicy)
                renderAppSelection()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun appSelectionDialogRow(app: InstalledApp, selectedPackages: MutableSet<String>): View {
        val checkbox = CheckBox(this).apply {
            isChecked = selectedPackages.contains(app.packageName)
            buttonTintList = ColorStateList.valueOf(getColor(R.color.teal))
            isClickable = false
            isFocusable = false
        }
        val labels = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(TextView(this@MainActivity).apply {
                text = app.label
                textSize = 14f
                setTextColor(getColor(R.color.ink))
                typeface = android.graphics.Typeface.DEFAULT_BOLD
            })
            addView(TextView(this@MainActivity).apply {
                text = app.packageName
                textSize = 11f
                setTextColor(getColor(R.color.ink_faint))
                maxLines = 1
            })
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            isClickable = true
            isFocusable = true
            setBackgroundResource(R.drawable.bg_app_row)
            setPadding(dp(8), dp(8), dp(12), dp(8))
            layoutParams = matchWidth(topMargin = 6)
            addView(checkbox, LinearLayout.LayoutParams(dp(44), dp(44)))
            addView(labels, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            setOnClickListener {
                checkbox.isChecked = !checkbox.isChecked
                if (checkbox.isChecked) {
                    selectedPackages.add(app.packageName)
                } else {
                    selectedPackages.remove(app.packageName)
                }
            }
        }
    }

    private fun updateRoutingSummary() {
        routingSummary.text = when (routingPolicy.mode) {
            AppRoutingMode.ALL -> getString(R.string.routing_all_summary)
            AppRoutingMode.ALLOWLIST -> resources.getQuantityString(
                R.plurals.routing_allowlist_summary,
                routingPolicy.packages.size,
                routingPolicy.packages.size
            )
            AppRoutingMode.BLOCKLIST -> resources.getQuantityString(
                R.plurals.routing_blocklist_summary,
                routingPolicy.packages.size,
                routingPolicy.packages.size
            )
        }
    }

    private fun matchWidth(topMargin: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            this.topMargin = dp(topMargin)
        }

    private fun space(width: Int): View = View(this).apply {
        layoutParams = LinearLayout.LayoutParams(width, 1)
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val EXPANDED_LAYOUT_MIN_WIDTH_DP = 600
        private const val VPN_REQUEST_CODE = 7070
        private const val NOTIFICATION_REQUEST_CODE = 7071
        private const val RULE_FILE_REQUEST_CODE = 7072
        private const val CONFIG_IMPORT_REQUEST_CODE = 7073
        private const val CONFIG_EXPORT_REQUEST_CODE = 7074
        private const val APP_ROUTING_ALL_ID = 1001
        private const val APP_ROUTING_ALLOWLIST_ID = 1002
        private const val APP_ROUTING_BLOCKLIST_ID = 1003
        private const val LIST_WHITELIST = "whitelist"
        private const val LIST_BLACKLIST = "blacklist"
        private val SOURCE_TYPES = listOf(SOURCE_LOCAL, SOURCE_REMOTE, SOURCE_GEOSITE, SOURCE_GEOIP)
        private val MATCH_TYPES = listOf("base", "suffix", "pan", "regex")
    }

    private data class UpstreamRow(
        val container: LinearLayout,
        val title: TextView,
        val name: EditText,
        val proxyUrl: EditText,
        val proxyValidation: TextView,
        val dnsResolve: CheckBox,
        val credit: EditText,
        val sleep: EditText,
        val correctDelay: EditText,
        val dialProxyFor: String,
        val remove: Boolean,
        val alias: EditText
    )

    private data class RuleSetRow(
        val container: LinearLayout,
        val title: TextView,
        val sourceType: Spinner,
        val matchType: Spinner,
        val geositeType: EditText,
        val path: EditText,
        val updateInterval: EditText,
        val updateIntervalLabel: TextView,
        val matchTypeLabel: TextView,
        val geositeTypeLabel: TextView,
        val pathLabel: TextView,
        val chooseFile: Button,
        val owner: UpstreamRow,
        val listKind: String
    )

    private data class HostSourceRow(
        val container: LinearLayout,
        val title: TextView,
        val sourceType: Spinner,
        val matchType: Spinner,
        val path: EditText,
        val updateInterval: EditText,
        val updateIntervalLabel: TextView,
        val credit: EditText,
        val chooseFile: Button
    )
}
