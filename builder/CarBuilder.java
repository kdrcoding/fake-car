import com.bmw.psdz.api.Configuration;
import com.bmw.psdz.api.Logic;
import com.bmw.psdz.api.ServiceLocator;
import com.bmw.psdz.api.data.FA;
import com.bmw.psdz.api.data.FP;
import com.bmw.psdz.api.data.ProcessClass;
import com.bmw.psdz.api.data.SGBMID;
import com.bmw.psdz.api.data.StandardSVK;
import com.bmw.psdz.api.data.caf.NCD;
import com.bmw.psdz.api.data.ecu.DiagAddress;
import com.bmw.psdz.api.data.ecu.ECUIdentifier;
import java.io.File;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Builds a Fake Car folder from your own E-Sys and PSdZData, with no car and no window.
 *
 * <p>In: car.txt (ISTEP_SHIPMENT, ISTEP_CURRENT, optional OUTDATED module addresses) and FA.xml (the vehicle order). Out: modules.txt, FP.xml, vcm.txt,
 * svk.txt, NCD\ and NCD_default\.
 *
 * <ol>
 * <li>Modules: for every module the KIS knows, the hardware part the build I-step lists whose fitting condition holds
 * for this order (type key, SA codes, type features, time criteria), the way the factory picks them.
 * <li>Software: what that part carries at the current I-step, and the CAFDs that belong to it.
 * <li>PSdZ: the FP, the SVK every module reports, and the factory coding of every CAFD for this order.
 * </ol>
 *
 * Run it with build-car.cmd.
 */
public final class CarBuilder {
    static final PrintStream out = System.out;
    static final String NO_SECURITY = "your PSdZData is incomplete: its security files (psdzdata\\security) are missing or do not work. Use a full PSdZData, or pick the right PSdZData folder in Options";

    public static void main(String[] a) {
        // PSdZ's security init fails on a Turkish Windows ("i".toUpperCase() is "İ" there) and E-Sys MAX users then got
        // "Invalid call for decryptVerifyXML" on every build (report R-4F8CD9, 2026-11-20). Set before any PSdZ class loads.
        java.util.Locale.setDefault(java.util.Locale.US);
        try {
            run(a);
        } catch (Throwable e) {
            // The builder's own words when it threw them (they say what to do); PSdZ's innermost message otherwise.
            Throwable c = e;
            boolean ours = e instanceof IllegalArgumentException && e.getStackTrace().length > 0
                    && CarBuilder.class.getName().equals(e.getStackTrace()[0].getClassName());
            if (!ours) while (c.getCause() != null) c = c.getCause();
            e.printStackTrace(System.err);
            out.println("Failed: " + String.valueOf(c.getMessage() != null ? c.getMessage() : c).split("\\R")[0]);
            System.exit(1);
        }
    }

    /** Fixed build-phase progress for Fake Car's progress bar. */
    static void progress(int percent, String what) {
        out.println("Progress: " + percent + " " + what);
    }

    static void run(String[] a) throws Exception {
        if (a.length == 2 && a[0].equals("--types")) {
            types(new File(a[1]).getAbsoluteFile());
            return;
        }
        if (a.length < 2) {
            out.println("usage: CarBuilder <car folder> <PSdZData parent folder, e.g. C:\\Data>");
            System.exit(2);
        }
        File car = new File(a[0]).getAbsoluteFile();
        File data = new File(a[1]).getAbsoluteFile();
        // PSdZ logs to the console; keep the output to what the builder says.
        System.setOut(new PrintStream(new java.io.ByteArrayOutputStream() {
            @Override
            public synchronized void write(byte[] b, int off, int len) {
            }
        }));
        // Until the build is done the car counts as not built (a build that stops halfway leaves nothing to start).
        write(new File(car, "building.txt"), "A build started and has not finished.\n");
        new File(car, "built.txt").delete();
        progress(0, "Preparing vehicle data");
        Map<String, String> settings = settings(new File(car, "car.txt"));
        String faXml = read(new File(car, "FA.xml"));
        Order order = new Order(faXml);
        // A car made from your own FA has no I-steps yet: take them from the KIS that knows its type key.
        Connection kisConnection;
        if (settings.get("ISTEP_CURRENT") == null) {
            kisConnection = isteps(data, order, new File(car, "car.txt"), settings);
        } else {
            String series = settings.get("ISTEP_CURRENT").substring(0, 4);
            File kisFolder = new File(data, "psdzdata\\kiswb\\" + series);
            if (!new File(kisFolder, "KIS.script").isFile())
                throw new IllegalArgumentException("your PSdZData has no " + series + " data, and this car needs it");
            kisConnection = open(kisFolder);
        }
        String build, current, series;
        List<String> modules;
        try (Connection c = kisConnection) {
            fit(c, settings);
            build = settings.get("ISTEP_SHIPMENT");
            current = settings.get("ISTEP_CURRENT");
            series = current.substring(0, 4);

            out.println("Car: " + settings.getOrDefault("NAME", car.getName()) + " (type " + order.type + ", built " + build + ", now " + current + ")");
            // OUTDATED=60,63: these modules keep the software they were built with, so E-Sys has something to flash.
            Set<Integer> outdated = new HashSet<>();
            for (String a1 : settings.getOrDefault("OUTDATED", "").split(",")) if (!a1.trim().isEmpty()) outdated.add(Integer.parseInt(a1.trim(), 16));
            progress(25, "Finding modules");
            modules = kis(c, order, build, current, outdated);
        }
        write(new File(car, "modules.txt"), String.join("\n", modules) + "\n");
        out.println("Modules: " + modules.size());
        if (modules.isEmpty()) throw new IllegalArgumentException("no modules for type " + order.type + " at " + build + ": its build date is probably outside the time this type was made");
        progress(50, "Starting PSdZ to create vehicle data");

        // PSdZ unlocks its encrypted data with the files in psdzdata\security. When they are missing it only logs
        // "ignored failure in security init" and fails much later with "Invalid call for decryptVerifyXML. Please call
        // init() method of %0 first". Say what is wrong before that.
        File sec = new File(data, "psdzdata\\security");
        boolean setA = new File(sec, "sec_security.lic").isFile() && new File(sec, "sec_certificates.pem").isFile() && new File(sec, "sec_keyreference.xml").isFile();
        boolean setB = new File(sec, "sec_auth_l3.xml").isFile() && new File(sec, "sec_auth_l4.xml").isFile() && new File(sec, "sec_transkeys.xml").isFile();
        if (!setA && !setB) throw new IllegalArgumentException(NO_SECURITY + " (" + sec + " has no sec_ files).");

        long t = System.currentTimeMillis();
        Configuration conf = ServiceLocator.locate(Configuration.class);
        conf.setRootDirectory(data);
        // The coding settings E-Sys hands PSdZ (some CAFs, the airbag module's among them, need them to calculate).
        java.util.Properties coding = new java.util.Properties();
        coding.setProperty("BusPrioritaet", "");
        coding.setProperty("DealerID", "0");
        coding.setProperty("DevSigAllowed", "true");
        coding.setProperty("PlantID", "04D2");
        coding.setProperty("ProgrammierGeraeteSeriennummer", "1000");
        coding.setProperty("ProgrammierGeraeteTyp", "01");
        coding.setProperty("ProgrammingSequenceUseCase", "DEVELOPMENT");
        coding.setProperty("Testereinsatzkennung", "FF");
        // E-Sys 25.10's PSdZ takes the plant ID in hex (PlantIDhex=04D2); E-Sys 22.06-24.06 take it in decimal
        // (PlantIDdec=1234) and fail on hex with "For input string: 04D2". Same plant, try both forms.
        try {
            conf.setPSdZProperties(coding);
        } catch (RuntimeException hexRefused) {
            coding.setProperty("PlantID", "1234");
            conf.setPSdZProperties(coding);
        }
        Logic logic = ServiceLocator.locate(Logic.class);
        out.println("PSdZ ready in " + (System.currentTimeMillis() - t) / 1000 + " s");

        FA fa = new FA();
        String faForParsing = faXml.substring(faXml.indexOf("<ns2:fa>"), faXml.indexOf("</ns2:fa>") + 9).replace("ns2:", "");
        fa.parseXML(faForParsing);
        FP fp;
        try {
            progress(63, "PSdZ generating vehicle data");
            fp = logic.generateFP(fa);
        } catch (RuntimeException e) {
            // Security files there but not usable (broken, or from another PSdZData): PSdZ's own words mean nothing to a user.
            for (Throwable c = e; c != null; c = c.getCause())
                if (String.valueOf(c.getMessage()).contains("decryptVerifyXML"))
                    throw new IllegalArgumentException(NO_SECURITY + ": PSdZ could not open its security files.", e);
            throw e;
        }
        write(new File(car, "FP.xml"), fp.getAsXML());
        write(new File(car, "vcm.txt"), "FA\t" + hex(fa.getAsStream()) + "\nFP\t" + hex(fp.getAsStream()) + "\n");

        StringBuilder svk = new StringBuilder();
        Map<Integer, ECUIdentifier> keys = new LinkedHashMap<>();
        Map<Integer, Set<SGBMID>> cafds = new LinkedHashMap<>();
        for (String line : modules) {
            String[] p = line.split("\t");
            int addr = Integer.parseInt(p[0], 16);
            List<SGBMID> ids = new ArrayList<>();
            for (String s : p[3].trim().split(" ")) ids.add(SGBMID.valueOf(s));
            svk.append(p[0]).append('\t').append(hex(new StandardSVK(ids.toArray(new SGBMID[0])).getAsStream())).append('\n');
            Set<SGBMID> mine = new LinkedHashSet<>();
            for (SGBMID id : ids) if (id.getProcessClass() == ProcessClass.PC_CAFD) mine.add(id);
            keys.put(addr, new ECUIdentifier(new DiagAddress(addr), p[1].substring(0, p[1].indexOf('-'))));
            if (!mine.isEmpty()) cafds.put(addr, mine);
        }
        write(new File(car, "svk.txt"), svk.toString());

        // Factory coding for this order, one CAFD at a time (one PSdZ cannot work out must not stop the others).
        // Where the order leaves a setting undecided, the CAF's own default values stand in.
        deleteTree(new File(car, "NCD"));
        deleteTree(new File(car, "NCD_default"));
        int coded = 0, defaults = 0, total = 0, done = 0;
        for (Set<SGBMID> ids : cafds.values()) total += ids.size();
        if (total == 0) progress(75, "Making factory coding");
        for (Map.Entry<Integer, Set<SGBMID>> e : cafds.entrySet()) {
            String folder = String.format("%02X", e.getKey());
            for (SGBMID id : e.getValue()) {
                progress(75 + 25 * done / Math.max(1, total),
                        "Making factory coding (" + (done + 1) + " of " + total + ")");
                NCD def = logic.generateNCD(new SGBMID[] {id})[0];
                write(new File(car, "NCD_default\\" + folder + "\\" + id.toFileName("ncd")), def.getAsMotorolaSString());
                Map<ECUIdentifier, Set<SGBMID>> one = new HashMap<>();
                one.put(keys.get(e.getKey()), Collections.singleton(id));
                NCD ncd = null;
                try {
                    Map<?, Map<SGBMID, NCD>> generated = logic.generateNoVehicleNCD(one, fa, fp);
                    for (Map<SGBMID, NCD> perModule : generated.values()) {
                        for (NCD n : perModule.values()) ncd = n;
                    }
                } catch (Exception ex) {
                    out.println("  " + folder + " " + id + ": default values (" + String.valueOf(ex.getMessage()).split("\\R")[0] + ")");
                }
                if (ncd == null) {
                    ncd = def;
                    defaults++;
                } else {
                    coded++;
                }
                write(new File(car, "NCD\\" + folder + "\\" + id.toFileName("ncd")), ncd.getAsMotorolaSString());
                done++;
            }
        }
        out.println("Coding: " + coded + " CAFDs coded for this order, " + defaults + " on default values");
        // What this build used: the I-steps (they may differ from car.txt on older PSdZData) and the PSdZData folder.
        write(new File(car, "built.txt"), "# Written by the builder: the I-steps your PSdZData has for this car, and where it was built from.\n"
                + "ISTEP_SHIPMENT=" + build + "\nISTEP_LAST=" + settings.get("ISTEP_LAST") + "\nISTEP_CURRENT=" + current + "\nDATA=" + data.getPath() + "\n");
        new File(car, "building.txt").delete();
        progress(100, "Done");
        out.println("Done: " + car);
        System.exit(0);
    }

    /** The order: type key, time criteria, SA codes and E/HO words, as written in FA.xml. */
    static final class Order {
        final String type;
        final String time;
        final Set<String> sa = new HashSet<>();
        final Set<String> words = new HashSet<>();
        final Map<String, String> features = new HashMap<>();

        Order(String xml) {
            type = attr(xml, "typeKey");
            String t = attr(xml, "timeCriteria");
            time = t.substring(2) + t.substring(0, 2); // MMYY -> YYMM, so it compares in order
            for (String tag : new String[] {"saCode", "eCode", "hoWord"}) {
                Matcher m = Pattern.compile("<(?:ns2:)?" + tag + ">([^<]+)<").matcher(xml);
                while (m.find()) (tag.equals("saCode") ? sa : words).add(m.group(1).trim());
            }
        }
    }

    /** The KIS of one I-step family, opened in place; files_readonly keeps HSQLDB from writing anything into PSdZData. */
    static Connection open(File kisFolder) throws Exception {
        if (!new File(kisFolder, "KIS.script").isFile()) throw new IllegalStateException("no KIS in " + kisFolder);
        Class.forName("org.hsqldb.jdbc.JDBCDriver");
        return DriverManager.getConnection("jdbc:hsqldb:file:" + new File(kisFolder, "KIS").getPath().replace('\\', '/')
                + ";readonly=true;ifexists=true;hsqldb.files_readonly=true", "SA", "");
    }

    static ResultSet query(Connection c, String sql, Object... params) throws SQLException {
        PreparedStatement statement = c.prepareStatement(sql);
        for (int i = 0; i < params.length; i++) statement.setObject(i + 1, params[i]);
        return statement.executeQuery();
    }

    /** ISTEP_SHIPMENT (the newest I-step at the order's time criteria) and ISTEP_CURRENT/LAST (the newest there is), added to car.txt. */
    static Connection isteps(File data, Order order, File carTxt, Map<String, String> settings) throws Exception {
        File kisRoot = new File(data, "psdzdata\\kiswb");
        if (!kisRoot.isDirectory()) throw new IllegalStateException("no KIS in " + data);
        String requestedFamily = settings.get("FAMILY");
        File[] families = requestedFamily == null || requestedFamily.isEmpty()
                ? kisRoot.listFiles(File::isDirectory)
                : new File[] {new File(kisRoot, requestedFamily)};
        if (families == null) throw new IllegalStateException("no KIS in " + data);
        String month = order.time.substring(0, 2) + "-" + order.time.substring(2); // YY-MM
        for (File family : families) {
            if (!new File(family, "KIS.script").isFile()) continue;
            Connection c = open(family);
            boolean selected = false;
            try {
                ResultSet r = query(c, "SELECT COUNT(*) FROM KIS.FAHRZEUGTYP WHERE NAME=?", order.type);
                if (!r.next() || r.getInt(1) == 0) continue;
                String pattern = family.getName() + "-__-__-___", shipment = null, current = null;
                r = query(c, "SELECT NAME FROM KIS.ISTUFE WHERE NAME LIKE '" + pattern + "' ORDER BY NAME");
                while (r.next()) {
                    String name = r.getString(1);
                    if (name.substring(5, 10).compareTo(month) <= 0) shipment = name;
                    current = name;
                }
                if (current == null) continue;
                if (shipment == null) shipment = current;
                settings.put("ISTEP_SHIPMENT", shipment);
                settings.put("ISTEP_LAST", current);
                settings.put("ISTEP_CURRENT", current);
                String text = read(carTxt);
                write(carTxt, text + (text.endsWith("\n") ? "" : "\n") + "ISTEP_SHIPMENT=" + shipment + "\nISTEP_LAST=" + current + "\nISTEP_CURRENT=" + current + "\n");
                selected = true;
                return c;
            } finally {
                if (!selected) c.close();
            }
        }
        throw new IllegalArgumentException("type key " + order.type + " is not in your PSdZData");
    }

    /**
     * Every car type in this PSdZData, for Fake Car's "Add a car" window. One line per I-step family ("istep", family,
     * its I-step months YY-MM,YY-MM,...) and per type ("type", family, body series, type key, body, engine, cc, drive, market, steering,
     * built from YYYY-MM, built until YYYY-MM).
     * Motorbike and other families Fake Car cannot be (K, X, NA) are left out.
     */
    static void types(File data) throws Exception {
        File[] families = new File(data, "psdzdata\\kiswb").listFiles(File::isDirectory);
        if (families == null) throw new IllegalStateException("no KIS in " + data);
        java.util.Arrays.sort(families);
        String[] keys = {"EBR", "KFORM", "MBR", "HUBR", "ANTR", "AUSF", "LENK"};
        for (File family : families) {
            String f = family.getName();
            if (f.startsWith("K") || f.startsWith("X") || f.startsWith("NA") || !new File(family, "KIS.script").isFile()) continue;
            try (Connection c = open(family)) {
                // The months the family has I-steps in (YY-MM): PSdZ takes only those as a vehicle order's build date.
                ResultSet r = query(c, "SELECT DISTINCT SUBSTRING(NAME, 6, 5) FROM KIS.ISTUFE WHERE NAME LIKE '" + f + "-__-__-___' ORDER BY 1");
                List<String> months = new ArrayList<>();
                while (r.next()) months.add(r.getString(1));
                if (months.isEmpty()) continue;
                out.println("istep\t" + f + "\t" + String.join(",", months));
                // Only types with a production time (TYPINHALT_VBA): the build date must fall in it, or the FA has no FP
                // and KIS finds no modules. Types without one (special variants) are left out.
                r = query(c, "SELECT F.NAME, F.TYPMERKMALE, MIN(V.VBA_VON), MAX(V.VBA_BIS) FROM KIS.FAHRZEUGTYP F"
                        + " JOIN KIS.TYPINHALT_VBA V ON V.FAHRZEUGTYP_ID=F.ID GROUP BY F.NAME, F.TYPMERKMALE ORDER BY F.NAME");
                while (r.next()) {
                    Map<String, String> features = new HashMap<>();
                    for (String kv : String.valueOf(r.getString(2)).split(";")) if (kv.contains("=")) features.put(kv.substring(0, kv.indexOf('=')), kv.substring(kv.indexOf('=') + 1));
                    if (features.get("EBR") == null || r.getString(3) == null) continue;
                    StringBuilder line = new StringBuilder("type\t" + f + "\t" + features.get("EBR") + "\t" + r.getString(1).trim());
                    for (int i = 1; i < keys.length; i++) line.append('\t').append(features.getOrDefault(keys[i], ""));
                    line.append('\t').append(r.getString(3), 0, 7).append('\t').append(r.getString(4) == null ? "2099-12" : r.getString(4).substring(0, 7));
                    out.println(line);
                }
            } catch (Exception e) {
                out.println("skipped\t" + f + "\t" + String.valueOf(e.getMessage()).split("\\R")[0]);
            }
        }
    }

    /**
     * Older PSdZData does not know the newest I-steps in car.txt: each one becomes the closest this KIS has (the newest at
     * or before it, else the oldest), with the shipment and last I-steps never after the current one. A car whose I-steps
     * are all there is built exactly as before.
     */
    static void fit(Connection c, Map<String, String> settings) throws Exception {
        String series = settings.get("ISTEP_CURRENT").substring(0, 4);
        List<String> names = new ArrayList<>();
        ResultSet r = query(c, "SELECT NAME FROM KIS.ISTUFE WHERE NAME LIKE '" + series + "-__-__-___' ORDER BY NAME");
        while (r.next()) names.add(r.getString(1));
        if (names.isEmpty()) throw new IllegalArgumentException("your PSdZData has no " + series + " I-steps");
        String current = closest(names, settings.get("ISTEP_CURRENT"));
        for (String key : new String[] {"ISTEP_SHIPMENT", "ISTEP_LAST", "ISTEP_CURRENT"}) {
            // The last I-step is only told to the tester (the KIS does not list every one): it just may not pass the current one.
            String wanted = settings.getOrDefault(key, current), got = key.equals("ISTEP_LAST") ? wanted : closest(names, wanted);
            if (got.compareTo(current) > 0) got = current;
            if (!got.equals(wanted)) out.println("I-step " + key + ": " + wanted + " is not in your PSdZData, using " + got);
            settings.put(key, got);
        }
    }

    static String closest(List<String> names, String wanted) {
        String best = names.get(0);
        for (String name : names) if (name.compareTo(wanted) <= 0) best = name;
        return best;
    }

    static List<String> kis(Connection c, Order order, String build, String current, Set<Integer> outdated) throws Exception {
        List<String> lines = new ArrayList<>();
        {
            ResultSet type = query(c, "SELECT TYPMERKMALE FROM KIS.FAHRZEUGTYP WHERE NAME=?", order.type);
            if (!type.next()) throw new IllegalArgumentException("type key " + order.type + " is not in the " + current.substring(0, 4) + " data");
            for (String kv : type.getString(1).split(";")) if (kv.contains("=")) order.features.put(kv.substring(0, kv.indexOf('=')), kv.substring(kv.indexOf('=') + 1));
            long built = istep(c, build);
            long builtMonth = istepMonth(c, build), currentMonth = istepMonth(c, current);
            Map<Integer, String[]> parts = new LinkedHashMap<>();
            ResultSet r = query(c, "SELECT B.NAME, B.DIAGNOSEADRESSE, L.NAME, CAST(E.BEDINGUNG AS VARCHAR(16000000)) FROM KIS.LOGISTISCHEVERWENDUNG L"
                    + " JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID LEFT JOIN KIS.EINSATZBEDINGUNG E ON E.ID=L.EINSATZBEDINGUNG_ID"
                    + " WHERE L.TEILCHARAKTER='BVI' AND L.ISTUFE_ID=? ORDER BY B.DIAGNOSEADRESSE, B.NAME, L.ID DESC", built);
            Set<String> done = new HashSet<>();
            while (r.next()) {
                String bntn = r.getString(1);
                if (done.contains(bntn) || parts.containsKey(r.getInt(2))) continue;
                String cond = r.getString(4);
                if (cond != null && !new Condition(cond, order).holds()) continue;
                done.add(bntn);
                parts.put(r.getInt(2), new String[] {bntn, r.getString(3)});
            }
            int checked = 0, total = parts.size();
            for (Map.Entry<Integer, String[]> e : parts.entrySet()) {
                progress(25 + 25 * checked / Math.max(1, total),
                        "Checking module " + e.getValue()[0] + " (" + (checked + 1) + " of " + total + ")");
                boolean isOutdated = outdated.contains(e.getKey());
                // Hardware follows the build step; software follows the latest patch in the current month.
                String line = software(c, e.getKey(), e.getValue()[0], e.getValue()[1],
                        isOutdated ? built : currentMonth, built,
                        isOutdated ? builtMonth : currentMonth, order);
                if (line != null) lines.add(line);
                checked++;
            }
        }
        return lines;
    }

    static long istep(Connection c, String name) throws Exception {
        ResultSet r = query(c, "SELECT ID FROM KIS.ISTUFE WHERE NAME=?", name);
        if (!r.next()) throw new IllegalArgumentException("I-step " + name + " is not in this PSdZData");
        return r.getLong(1);
    }

    static long istepMonth(Connection c, String name) throws Exception {
        String month = name.substring(0, name.lastIndexOf('-') + 1) + "___";
        ResultSet r = query(c, "SELECT ID FROM KIS.ISTUFE WHERE NAME LIKE ? ORDER BY NAME DESC", month);
        if (!r.next()) throw new IllegalArgumentException("I-step month " + month + " is not in this PSdZData");
        return r.getLong(1);
    }

    static final Map<Integer, String> PC = new HashMap<>();
    static {
        PC.put(1, "HWEL"); PC.put(2, "HWAP"); PC.put(3, "HWFR"); PC.put(4, "GWTB"); PC.put(5, "CAFD"); PC.put(6, "BTLD");
        PC.put(7, "FLSL"); PC.put(8, "SWFL"); PC.put(9, "SWFF"); PC.put(12, "IBAD"); PC.put(13, "SWFK"); PC.put(16, "FCFA"); PC.put(26, "TLRT");
    }

    /** addr, module, part, then its software at the current I-step (newest per unit) and the CAFDs of that software. */
    static String software(Connection c, int addr, String bntn, String part, long now, long hardwareStep, long beguNow, Order order) throws Exception {
        String pattern = relatedPartPattern(bntn, part);
        String sql = "SELECT L.ID,L.NAME FROM KIS.LOGISTISCHEVERWENDUNG L JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID"
                + " WHERE B.NAME=? AND L.TEILCHARAKTER='BVI' AND L.ISTUFE_ID<=?"
                + " AND (L.NAME=? OR L.NAME LIKE ? ESCAPE '!'"
                + (pattern == null ? "" : " OR L.NAME LIKE ? ESCAPE '!'") + ")"
                + " ORDER BY L.ISTUFE_ID DESC, L.ID DESC";
        ResultSet lr = pattern == null
                ? query(c, sql, bntn, now, part, likeLiteral(part) + "!_%")
                : query(c, sql, bntn, now, part, likeLiteral(part) + "!_%", pattern);
        while (lr.next()) {
            if (!relatedPart(bntn, part, lr.getString(2))) continue;
            ResultSet tr = query(c, "SELECT T.ID, E.WERT, E.SGBM_NR, E.MAJOR, E.MINOR, E.PATCH FROM KIS.TECHNISCHEVERWENDUNG T"
                    + " JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=T.TE_ID WHERE T.LV_ID=? AND T.ISTUFE_ID<=? ORDER BY T.ISTUFE_ID, E.MAJOR, E.MINOR, E.PATCH",
                    lr.getLong(1), now);
            Map<String, String> units = new LinkedHashMap<>();
            Map<String, Long> unitTv = new LinkedHashMap<>();
            while (tr.next()) {
                String key = tr.getInt(2) + "/" + tr.getString(3).trim();
                units.put(key, id(tr.getInt(2), tr.getString(3), tr.getInt(4), tr.getInt(5), tr.getInt(6)));
                unitTv.put(key, tr.getLong(1));
            }
            if (units.isEmpty()) continue;
            Set<String> hardware = hardware(c, bntn, part, hardwareStep, order);
            if (!hardware.isEmpty()) {
                for (String key : new ArrayList<>(units.keySet())) if (key.startsWith("1/") || key.startsWith("2/") || key.startsWith("3/")) {
                    units.remove(key);
                    unitTv.remove(key);
                }
            }
            Set<String> ids = new LinkedHashSet<>();
            for (int pc : new int[] {1, 2, 3, 6, 7, 8, 9, 12, 13, 16, 4}) {
                if (pc <= 3 && !hardware.isEmpty()) {
                    ids.addAll(hardware);
                    continue;
                }
                for (Map.Entry<String, String> u : units.entrySet()) if (u.getKey().startsWith(pc + "/")) ids.add(u.getValue());
            }
            ids.addAll(gwtb(c, bntn, now, order));
            ids.addAll(begu(c, bntn, part, beguNow, order));
            Map<String, String> cafds = new LinkedHashMap<>();
            for (Long unit : unitTv.values()) {
                ResultSet cr = query(c, "SELECT E.SGBM_NR, E.MAJOR, E.MINOR, E.PATCH FROM KIS.CAFMAPPING M JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=M.CAF"
                        + " WHERE M.TV_SWE=? ORDER BY E.MAJOR, E.MINOR, E.PATCH", unit);
                while (cr.next()) cafds.put(cr.getString(1).trim(), id(5, cr.getString(1), cr.getInt(2), cr.getInt(3), cr.getInt(4)));
            }
            ids.addAll(cafds.values());
            return String.format("%02X", addr) + "\t" + bntn + "\t" + part + "\t" + String.join(" ", ids);
        }
        return null;
    }

    static Set<String> hardware(Connection c, String bntn, String part, long now, Order order) throws Exception {
        String pattern = relatedPartPattern(bntn, part);
        String sql = "SELECT L.ID,L.NAME,CAST(C.BEDINGUNG AS VARCHAR(16000000)) FROM KIS.LOGISTISCHEVERWENDUNG L"
                + " JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID LEFT JOIN KIS.EINSATZBEDINGUNG C ON C.ID=L.EINSATZBEDINGUNG_ID"
                + " WHERE B.NAME=? AND L.TEILCHARAKTER='BVI' AND L.ISTUFE_ID<=?"
                + " AND (L.NAME=? OR L.NAME LIKE ? ESCAPE '!'"
                + (pattern == null ? "" : " OR L.NAME LIKE ? ESCAPE '!'") + ")"
                + " ORDER BY L.ISTUFE_ID DESC,L.ID DESC";
        ResultSet parts = pattern == null
                ? query(c, sql, bntn, now, part, likeLiteral(part) + "!_%")
                : query(c, sql, bntn, now, part, likeLiteral(part) + "!_%", pattern);
        while (parts.next()) {
            if (!relatedPart(bntn, part, parts.getString(2))) continue;
            String condition = parts.getString(3);
            if (condition != null && !new Condition(condition, order).holds()) continue;
            ResultSet entries = query(c, "SELECT E.WERT,E.SGBM_NR,E.MAJOR,E.MINOR,E.PATCH FROM KIS.TECHNISCHEVERWENDUNG T"
                    + " JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=T.TE_ID WHERE T.LV_ID=? AND T.ISTUFE_ID<=? AND E.WERT IN (1,2,3)"
                    + " ORDER BY T.ISTUFE_ID DESC,E.WERT,E.SGBM_NR,E.MAJOR DESC,E.MINOR DESC,E.PATCH DESC", parts.getLong(1), now);
            Map<String, String> assignments = new LinkedHashMap<>();
            while (entries.next()) {
                int pc = entries.getInt(1);
                String number = entries.getString(2).trim();
                assignments.putIfAbsent(pc + "/" + number, id(pc, number, entries.getInt(3), entries.getInt(4), entries.getInt(5)));
            }
            if (!assignments.isEmpty()) return new LinkedHashSet<>(assignments.values());
        }
        return Collections.emptySet();
    }

    static Set<String> gwtb(Connection c, String bntn, long now, Order order) throws Exception {
        String sql = "SELECT L.ID,CAST(C.BEDINGUNG AS VARCHAR(16000000)) FROM KIS.LOGISTISCHEVERWENDUNG L"
                + " JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID LEFT JOIN KIS.EINSATZBEDINGUNG C ON C.ID=L.EINSATZBEDINGUNG_ID"
                + " WHERE B.NAME=? AND L.TEILCHARAKTER='GWT' AND L.ISTUFE_ID<=?"
                + " AND EXISTS (SELECT 1 FROM KIS.TECHNISCHEVERWENDUNG T JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=T.TE_ID"
                + " WHERE T.LV_ID=L.ID AND T.ISTUFE_ID<=? AND E.WERT=4)"
                + " ORDER BY L.ISTUFE_ID DESC,L.ID DESC";
        ResultSet parts = query(c, sql, bntn, now, now);
        while (parts.next()) {
            String condition = parts.getString(2);
            if (condition != null && !new Condition(condition, order).holds()) continue;
            ResultSet entries = query(c, "SELECT E.SGBM_NR,E.MAJOR,E.MINOR,E.PATCH FROM KIS.TECHNISCHEVERWENDUNG T"
                    + " JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=T.TE_ID WHERE T.LV_ID=? AND T.ISTUFE_ID<=? AND E.WERT=4"
                    + " ORDER BY T.ISTUFE_ID DESC,E.SGBM_NR,E.MAJOR DESC,E.MINOR DESC,E.PATCH DESC", parts.getLong(1), now);
            Map<String, String> assignments = new LinkedHashMap<>();
            while (entries.next()) {
                String number = entries.getString(1).trim();
                assignments.putIfAbsent(number, id(4, number, entries.getInt(2), entries.getInt(3), entries.getInt(4)));
            }
            if (!assignments.isEmpty()) return new LinkedHashSet<>(assignments.values());
        }
        return Collections.emptySet();
    }

    static Set<String> begu(Connection c, String bntn, String part, long now, Order order) throws Exception {
        String sql = "SELECT L.BEGUSTORE_ID,L.NAME FROM KIS.LOGISTISCHEVERWENDUNG L"
                + " JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID"
                + " WHERE B.NAME=? AND L.TEILCHARAKTER='BVI' AND L.ISTUFE_ID<=? AND L.BEGUSTORE_ID IS NOT NULL AND L.BEGUSTORE_ID<>0"
                + " AND (L.NAME=? OR L.NAME LIKE ? ESCAPE '!'"
                + (relatedPartPattern(bntn, part) == null ? "" : " OR L.NAME LIKE ? ESCAPE '!'") + ")"
                + " AND EXISTS (SELECT 1 FROM KIS.BEGUVERWENDUNG V JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=V.TECHNISCHEEINHEIT_ID"
                + " WHERE V.BEGUSTORE_ID=L.BEGUSTORE_ID AND E.WERT=13)"
                + " ORDER BY L.ISTUFE_ID DESC, L.ID DESC";
        String pattern = relatedPartPattern(bntn, part);
        ResultSet stores = pattern == null
                ? query(c, sql, bntn, now, part, likeLiteral(part) + "!_%")
                : query(c, sql, bntn, now, part, likeLiteral(part) + "!_%", pattern);
        Set<Long> seen = new HashSet<>();
        while (stores.next()) {
            if (!relatedPart(bntn, part, stores.getString(2))) continue;
            long store = stores.getLong(1);
            if (!seen.add(store)) continue;
            ResultSet entries = query(c, "SELECT E.WERT,E.SGBM_NR,E.MAJOR,E.MINOR,E.PATCH,C.BEDINGUNG"
                    + " FROM KIS.BEGUVERWENDUNG V JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=V.TECHNISCHEEINHEIT_ID"
                    + " LEFT JOIN KIS.EINSATZBEDINGUNG C ON C.ID=V.EINSATZBEDINGUNG_ID"
                    + " WHERE V.BEGUSTORE_ID=? AND E.WERT=13 ORDER BY E.SGBM_NR,E.MAJOR,E.MINOR,E.PATCH", store);
            Map<String, String> assignments = new LinkedHashMap<>();
            while (entries.next()) {
                String condition = entries.getString(6);
                if (condition != null && !new Condition(condition, order).holds()) continue;
                String key = entries.getInt(1) + "/" + entries.getString(2).trim();
                assignments.put(key, id(entries.getInt(1), entries.getString(2), entries.getInt(3), entries.getInt(4), entries.getInt(5)));
            }
            if (!assignments.isEmpty()) return new LinkedHashSet<>(assignments.values());
        }
        return Collections.emptySet();
    }

    static String relatedPartPattern(String bntn, String part) {
        String prefix = bntn + "-";
        if (!part.startsWith(prefix)) return null;
        String base = part.substring(prefix.length());
        int split = base.lastIndexOf('_');
        if (split < 1) return null;
        String family = base.substring(0, base.indexOf('_')), variant = base.substring(split + 1);
        return likeLiteral(prefix + family + "_") + "%!_" + likeLiteral(variant);
    }

    static String likeLiteral(String value) {
        return value.replace("!", "!!").replace("%", "!%").replace("_", "!_");
    }

    static boolean relatedPart(String bntn, String part, String candidate) {
        String prefix = bntn + "-";
        if (!part.startsWith(prefix) || !candidate.startsWith(prefix)) return part.equals(candidate);
        String base = part.substring(prefix.length()), name = candidate.substring(prefix.length());
        if (name.equals(base) || name.startsWith(base + "_")) return true;
        int split = base.lastIndexOf('_');
        if (split < 1) return false;
        String family = base.substring(0, base.indexOf('_')), variant = base.substring(split + 1);
        return name.startsWith(family + "_") && name.endsWith("_" + variant);
    }

    static String id(int pc, String nr, int ma, int mi, int pa) {
        return String.format("%s_%s_%03d_%03d_%03d", PC.getOrDefault(pc, "PC" + pc), nr.trim(), ma, mi, pa);
    }

    /**
     * A KIS fitting condition: "/" or, "+" and, "-" not, brackets. Atoms: T&lt;type key&gt;, S&lt;SA&gt;, KEY=VALUE type
     * features (a trailing * matches the start), &lt;MMYY / &gt;=MMYY time criteria, E/HO words, TRUE/FALSE.
     */
    static final class Condition {
        static final Map<String, String> ALIAS = new HashMap<>();
        static {
            ALIAS.put("TYP_ENTW_BEZ", "EBR"); ALIAS.put("TYP_LAND", "AUSF"); ALIAS.put("TYP_MOTBR", "MBR"); ALIAS.put("TYP_KRAFTST", "KRST");
            ALIAS.put("TYP_FZGZUSDIFF", "FZDIFF"); ALIAS.put("TYP_MOTOR", "MOT"); ALIAS.put("TYP_LKLAS", "MLK"); ALIAS.put("TYP_DERFAM", "DERFAM");
            ALIAS.put("TYP_ANTRIEB", "ANTR"); ALIAS.put("TYP_HYBRID", "HYB"); ALIAS.put("TYP_LENKUNG", "LENK"); ALIAS.put("TYP_MOTUE", "MUE");
        }

        final String s;
        final Order order;
        int i;

        Condition(String s, Order order) {
            this.s = s.replaceAll("\\s+", "");
            this.order = order;
        }

        boolean holds() {
            boolean v = or();
            if (i != s.length()) throw new IllegalStateException("cannot read condition at " + i);
            return v;
        }

        boolean or() {
            boolean v = and();
            while (i < s.length() && s.charAt(i) == '/') { i++; v |= and(); }
            return v;
        }

        boolean and() {
            boolean v = not();
            while (i < s.length() && s.charAt(i) == '+') { i++; v &= not(); }
            return v;
        }

        boolean not() {
            if (s.charAt(i) == '-') { i++; return !not(); }
            if (s.charAt(i) == '(') {
                i++;
                boolean v = or();
                i++; // ')'
                return v;
            }
            return atom();
        }

        boolean atom() {
            int start = i;
            while (i < s.length() && "/+()".indexOf(s.charAt(i)) < 0 && !(s.charAt(i) == '-' && i > start)) i++;
            String t = s.substring(start, i);
            if (t.equals("TRUE")) return true;
            if (t.equals("FALSE")) return false;
            if (t.startsWith(">=")) return order.time.compareTo(yymm(t.substring(2))) >= 0;
            if (t.startsWith("<=")) return order.time.compareTo(yymm(t.substring(2))) <= 0;
            if (t.startsWith("<")) return order.time.compareTo(yymm(t.substring(1))) < 0;
            if (t.startsWith(">")) return order.time.compareTo(yymm(t.substring(1))) > 0;
            if (t.contains("=")) return feature(t.substring(0, t.indexOf('=')), t.substring(t.indexOf('=') + 1));
            if (t.length() == 5 && t.charAt(0) == 'T') return t.substring(1).equals(order.type);
            if (t.length() == 4 && t.charAt(0) == 'S') return order.sa.contains(t.substring(1));
            return order.words.contains(t) || order.words.contains(t.substring(1));
        }

        boolean feature(String key, String want) {
            Map<String, String> f = order.features;
            String have;
            if (key.equals("TYP_LCI")) have = "BAS".equals(f.get("LZI")) ? "BAS" : "LCI";
            else if (key.equals("TYP_MONT_LAND")) have = "W_" + f.getOrDefault("MLAND", "");
            else have = f.get(ALIAS.getOrDefault(key, key));
            if (have == null) return false;
            // KIS stores DG20 in TYPMERKMALE but writes the same family as DF_G20 in fitting conditions.
            if (key.equals("TYP_DERFAM") && want.startsWith("DF_") && have.equals("D" + want.substring(3))) return true;
            if (want.endsWith("*")) return have.startsWith(want.substring(0, want.length() - 1));
            // Some values carry a prefix the type data leaves off (TYP_FZGZUSDIFF=FZD_02 against FZDIFF=02).
            return have.equals(want) || want.endsWith("_" + have);
        }

        static String yymm(String mmyy) {
            return mmyy.substring(2) + mmyy.substring(0, 2);
        }
    }

    static String attr(String xml, String name) {
        Matcher m = Pattern.compile(name + "=\"([^\"]*)\"").matcher(xml);
        if (!m.find()) throw new IllegalArgumentException("FA.xml has no " + name);
        return m.group(1);
    }

    static Map<String, String> settings(File f) throws Exception {
        Map<String, String> d = new HashMap<>();
        for (String line : Files.readAllLines(f.toPath(), StandardCharsets.UTF_8)) {
            int i = line.indexOf('=');
            if (i > 0 && !line.startsWith("#")) d.put(line.substring(0, i).trim(), line.substring(i + 1).trim());
        }
        return d;
    }

    static String read(File f) throws Exception {
        return new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
    }

    static void write(File f, String text) throws Exception {
        f.getParentFile().mkdirs();
        Files.write(f.toPath(), text.getBytes(StandardCharsets.UTF_8));
    }

    static void deleteTree(File f) {
        File[] kids = f.listFiles();
        if (kids != null) for (File k : kids) deleteTree(k);
        f.delete();
    }

    static String hex(byte[] b) {
        StringBuilder s = new StringBuilder();
        for (byte x : b) s.append(String.format("%02X", x));
        return s.toString();
    }
}
