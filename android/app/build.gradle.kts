plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "br.italo.italophone"
    compileSdk = 36
    defaultConfig {
        applicationId = "br.italo.italophone"
        minSdk = 30
        targetSdk = 36
        versionCode = 4
        versionName = "4"
    }
    buildTypes {
        release {
            isMinifyEnabled = true
            signingConfig = signingConfigs.getByName("debug")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlin { jvmToolchain(17) }
}
