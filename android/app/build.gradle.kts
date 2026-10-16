plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

dependencies {
    implementation("org.yaml:snakeyaml:2.3")
    implementation("androidx.work:work-runtime-ktx:2.9.1")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}

android {
    namespace = "io.tcproute.android"
    compileSdk = 35
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "io.tcproute.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 113
        versionName = "1.1"
        testInstrumentationRunner = "io.tcproute.android.NativeBridgeInstrumentation"
        ndk {
            abiFilters += "arm64-v8a"
        }

    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    packaging {
        jniLibs {
            // iCloud conflict copies are generated artifacts, not loadable libraries.
            excludes += "**/* *.so"
            useLegacyPackaging = true
        }
    }

    sourceSets {
        getByName("main").assets.srcDir("../../assets")
    }

    bundle {
        language {
            enableSplit = false
        }
    }
}
