package io.tcproute.android

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import java.util.concurrent.TimeUnit

class SubscriptionWorker(context: Context, parameters: WorkerParameters) : Worker(context, parameters) {
    override fun doWork(): Result = try {
        synchronized(SubscriptionStore) {
            val source = SubscriptionStore.load(applicationContext).firstOrNull { it.id == inputData.getString("source") }
            if (source != null && source.intervalMinutes > 0 && System.currentTimeMillis() - source.updatedAt >= TimeUnit.MINUTES.toMillis(source.intervalMinutes))
                SubscriptionStore.refresh(applicationContext, source)
        }
        Result.success()
    } catch (_: Exception) { Result.retry() }

    companion object {
        fun schedule(context: Context, source: AndroidSubscription, deleted: Boolean = false) {
            val manager = WorkManager.getInstance(context)
            val key = "TcpRoute2-subscription-${source.id}"
            if (deleted || source.intervalMinutes == 0L) manager.cancelUniqueWork(key)
            else manager.enqueueUniquePeriodicWork(key, ExistingPeriodicWorkPolicy.UPDATE,
                PeriodicWorkRequestBuilder<SubscriptionWorker>(source.intervalMinutes, TimeUnit.MINUTES)
                    .setInputData(workDataOf("source" to source.id))
                    .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build())
        }
    }
}
