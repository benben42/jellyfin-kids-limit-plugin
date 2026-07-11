plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.kidslimit.coins"
    compileSdk = 34

    defaultConfig {
        applicationId = "org.kidslimit.coins"
        minSdk = 21
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        val kidPageUrl = (project.findProperty("KID_PAGE_URL") as? String).orEmpty()
        buildConfigField("String", "KID_PAGE_URL", "\"$kidPageUrl\"")
    }

    buildFeatures {
        buildConfig = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}
