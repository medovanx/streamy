package dev.streamy.helper;

import android.annotation.SuppressLint;
import android.app.usage.StorageStats;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Build;
import android.os.IBinder;
import android.os.Looper;
import android.util.Base64;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.PrintStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.List;

/**
 * Streamy's phone-side helper, started over adb like scrcpy-server:
 *
 *   CLASSPATH=/data/local/tmp/streamy-helper.jar app_process / dev.streamy.helper.Main apps [sizes]
 *   CLASSPATH=... app_process / dev.streamy.helper.Main icons 96 com.example.a com.example.b
 *
 * "apps" prints one tab-separated line per installed app:
 *   package, label, versionName, versionCode, flags (s = system, d = disabled, l = has a launcher activity),
 *   first install (ms), last update (ms), APK bytes, app bytes, data bytes, cache bytes (-1 when unknown;
 *   "apps sizes" measures them, which is slower).
 * "icons" prints "package TAB base64 PNG" per package. Everything runs as the shell user; nothing is installed.
 */
public final class Main {
    private static final String SHELL = "com.android.shell";

    private Main() {
    }

    public static void main(String[] args) throws Exception {
        PrintStream out = new PrintStream(System.out, false, "UTF-8");
        if (args.length == 0) {
            out.println("usage: apps | icons SIZE PACKAGE...");
            out.flush();
            return;
        }
        Context context = systemContext();
        if ("icons".equals(args[0])) {
            // Icons that draw text (Samsung's calendar and clock show the date) need the fonts, which a zygote
            // would have preloaded. Load them the way Zygote does, once, before any drawing.
            try {
                Method preload = android.graphics.Typeface.class.getDeclaredMethod("loadPreinstalledSystemFontMap");
                preload.setAccessible(true);
                preload.invoke(null);
            } catch (Throwable ignored) {
                // not on this Android version
            }
        }
        PackageManager pm = context.getPackageManager();
        if ("apps".equals(args[0])) {
            apps(pm, out, args.length > 1 && "sizes".equals(args[1]));
        } else if ("icons".equals(args[0])) {
            int size = Integer.parseInt(args[1]);
            for (int i = 2; i < args.length; i++) {
                icon(pm, args[i], size, out);
                // Flushed per icon: an icon that kills the process (native crash) costs only itself; the PC
                // side starts again after it.
                out.flush();
            }
        }
        out.flush();
        // app_process keeps a few binder threads alive; leave at once.
        System.exit(0);
    }

    private static void apps(PackageManager pm, PrintStream out, boolean withSizes) {
        // Sizes come from the storage service, which takes seconds per hundred apps when its cache is cold.
        Object stats = withSizes ? storageStatsService() : null;
        @SuppressLint("QueryPermissionsNeeded")
        List<PackageInfo> packages = pm.getInstalledPackages(0);
        for (PackageInfo p : packages) {
            ApplicationInfo ai = p.applicationInfo;
            if (ai == null) {
                continue;
            }
            StringBuilder flags = new StringBuilder();
            if ((ai.flags & ApplicationInfo.FLAG_SYSTEM) != 0) {
                flags.append('s');
            }
            // DISABLED_UNTIL_USED (4): Samsung puts unused apps to sleep this way; they wake when opened, so they are
            // not shown as disabled.
            if (!ai.enabled && enabledSetting(pm, p.packageName) != 4) {
                flags.append('d');
            }
            if (pm.getLaunchIntentForPackage(p.packageName) != null) {
                flags.append('l');
            }
            long apk = length(ai.sourceDir);
            if (ai.splitSourceDirs != null) {
                for (String s : ai.splitSourceDirs) {
                    apk += length(s);
                }
            }
            long appBytes = -1, dataBytes = -1, cacheBytes = -1;
            StorageStats st = queryStats(stats, p.packageName);
            if (st != null) {
                appBytes = st.getAppBytes();
                dataBytes = st.getDataBytes();
                cacheBytes = st.getCacheBytes();
            }
            long versionCode = Build.VERSION.SDK_INT >= 28 ? p.getLongVersionCode() : p.versionCode;
            out.print(p.packageName);
            out.print('\t');
            out.print(clean(String.valueOf(ai.loadLabel(pm))));
            out.print('\t');
            out.print(clean(p.versionName == null ? "" : p.versionName));
            out.print('\t');
            out.print(versionCode);
            out.print('\t');
            out.print(flags);
            out.print('\t');
            out.print(p.firstInstallTime);
            out.print('\t');
            out.print(p.lastUpdateTime);
            out.print('\t');
            out.print(apk);
            out.print('\t');
            out.print(appBytes);
            out.print('\t');
            out.print(dataBytes);
            out.print('\t');
            out.print(cacheBytes);
            out.print('\n');
        }
    }

    private static void icon(PackageManager pm, String pkg, int size, PrintStream out) {
        try {
            // The app's own icon resource, at a high density. getApplicationIcon would return Samsung's live
            // calendar and clock icons, which draw text and abort a process without preloaded fonts.
            Drawable d = null;
            ApplicationInfo ai = pm.getApplicationInfo(pkg, 0);
            if (ai.icon != 0) {
                try {
                    d = pm.getResourcesForApplication(ai).getDrawableForDensity(ai.icon, 480, null);
                } catch (Throwable ignored) {
                    d = null;
                }
            }
            if (d == null) {
                d = pm.getDefaultActivityIcon();
            }
            Bitmap b = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
            Canvas c = new Canvas(b);
            d.setBounds(0, 0, size, size);
            d.draw(c);
            ByteArrayOutputStream png = new ByteArrayOutputStream();
            b.compress(Bitmap.CompressFormat.PNG, 100, png);
            out.print(pkg);
            out.print('\t');
            out.print(Base64.encodeToString(png.toByteArray(), Base64.NO_WRAP));
            out.print('\n');
        } catch (Throwable t) {
            out.print(pkg);
            out.print("\t-\n");
        }
    }

    private static int enabledSetting(PackageManager pm, String pkg) {
        try {
            return pm.getApplicationEnabledSetting(pkg);
        } catch (Throwable t) {
            return -1;
        }
    }

    private static String clean(String s) {
        return s.replace('\t', ' ').replace('\n', ' ').replace('\r', ' ');
    }

    private static long length(String path) {
        return path == null ? 0 : new File(path).length();
    }

    // ---- Storage sizes: IStorageStatsManager called directly with the shell's package name ----------------

    private static Object storageStatsService() {
        try {
            Class<?> sm = Class.forName("android.os.ServiceManager");
            IBinder binder = (IBinder) sm.getMethod("getService", String.class).invoke(null, "storagestats");
            Class<?> stub = Class.forName("android.app.usage.IStorageStatsManager$Stub");
            return stub.getMethod("asInterface", IBinder.class).invoke(null, binder);
        } catch (Throwable t) {
            return null;
        }
    }

    private static Method queryMethod;

    private static StorageStats queryStats(Object service, String pkg) {
        if (service == null) {
            return null;
        }
        try {
            if (queryMethod == null) {
                for (Method m : service.getClass().getMethods()) {
                    if (m.getName().equals("queryStatsForPackage") && m.getParameterTypes().length == 4) {
                        queryMethod = m;
                    }
                }
            }
            if (queryMethod == null) {
                return null;
            }
            return (StorageStats) queryMethod.invoke(service, null, pkg, 0, SHELL);
        } catch (Throwable t) {
            return null;
        }
    }

    // ---- A Context in a plain app_process, the way scrcpy-server gets one --------------------------------

    private static Context systemContext() throws Exception {
        if (Looper.getMainLooper() == null) {
            Looper.prepareMainLooper();
        }
        Class<?> at = Class.forName("android.app.ActivityThread");
        Constructor<?> ctor = at.getDeclaredConstructor();
        ctor.setAccessible(true);
        Object thread = ctor.newInstance();
        Field current = at.getDeclaredField("sCurrentActivityThread");
        current.setAccessible(true);
        current.set(null, thread);
        // Samsung (and Android 12+) read the configuration through this controller; a bare ActivityThread has none.
        try {
            Class<?> cc = Class.forName("android.app.ConfigurationController");
            Class<?> internal = Class.forName("android.app.ActivityThreadInternal");
            Constructor<?> ccCtor = cc.getDeclaredConstructor(internal);
            ccCtor.setAccessible(true);
            Field controller = at.getDeclaredField("mConfigurationController");
            controller.setAccessible(true);
            controller.set(thread, ccCtor.newInstance(thread));
        } catch (Throwable ignored) {
            // older Android: not needed
        }
        Method getSystemContext = at.getDeclaredMethod("getSystemContext");
        Context base = (Context) getSystemContext.invoke(thread);
        return new ContextWrapper(base) {
            @Override
            public String getPackageName() {
                return SHELL;
            }

            @Override
            public String getOpPackageName() {
                return SHELL;
            }
        };
    }
}
