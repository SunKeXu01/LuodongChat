package com.skx.chatgptconnector

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AndroidUpdateServiceTest {
    @Test
    fun comparesPreviewVersions() {
        assertTrue(AndroidUpdateService.isNewer("0.1.0-preview.18", "0.1.0-preview.17"))
        assertFalse(AndroidUpdateService.isNewer("0.1.0-preview.18", "0.1.0-preview.18"))
        assertFalse(AndroidUpdateService.isNewer("0.1.0-preview.17", "0.1.0-preview.18"))
    }
}
