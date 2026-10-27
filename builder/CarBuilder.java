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
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
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
 * <p>In: car.txt (ISTEP_SHIPMENT, ISTEP_CURRENT) and FA.xml (the vehicle order). Out: modules.txt, FP.xml, vcm.txt,
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

    public static void main(String[] a) {
        try {
            run(a);
        } catch (Throwable e) {
            Throwable c = e;
            while (c.getCause() != null) c = c.getCause();
            out.println("Failed: " + String.valueOf(c.getMessage() != null ? c.getMessage() : c).split("\\R")[0]);
            System.exit(1);
        }
    }

    static void run(String[] a) throws Exception {
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
        Map<String, String> settings = settings(new File(car, "car.txt"));
        String faXml = read(new File(car, "FA.xml"));
        Order order = new Order(faXml);
        String build = settings.get("ISTEP_SHIPMENT"), current = settings.get("ISTEP_CURRENT");
        String series = current.substring(0, 4);

        out.println("Car: " + settings.getOrDefault("NAME", car.getName()) + " (type " + order.type + ", built " + build + ", now " + current + ")");
        List<String> modules = kis(new File(data, "psdzdata\\kiswb\\" + series), order, build, current);
        write(new File(car, "modules.txt"), String.join("\n", modules) + "\n");
        out.println("Modules: " + modules.size());

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
        conf.setPSdZProperties(coding);
        Logic logic = ServiceLocator.locate(Logic.class);
        out.println("PSdZ ready in " + (System.currentTimeMillis() - t) / 1000 + " s");

        FA fa = new FA();
        fa.parseXML(faXml.substring(faXml.indexOf("<ns2:fa>"), faXml.indexOf("</ns2:fa>") + 9).replace("ns2:", ""));
        FP fp = logic.generateFP(fa);
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
        int coded = 0, defaults = 0;
        for (Map.Entry<Integer, Set<SGBMID>> e : cafds.entrySet()) {
            String folder = String.format("%02X", e.getKey());
            for (SGBMID id : e.getValue()) {
                NCD def = logic.generateNCD(new SGBMID[] {id})[0];
                write(new File(car, "NCD_default\\" + folder + "\\" + id.toFileName("ncd")), def.getAsMotorolaSString());
                Map<ECUIdentifier, Set<SGBMID>> one = new HashMap<>();
                one.put(keys.get(e.getKey()), Collections.singleton(id));
                NCD ncd = null;
                try {
                    for (Map<SGBMID, NCD> perModule : logic.generateNoVehicleNCD(one, fa, fp).values()) for (NCD n : perModule.values()) ncd = n;
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
            }
        }
        out.println("Coding: " + coded + " CAFDs coded for this order, " + defaults + " on default values");
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

    static List<String> kis(File kisFolder, Order order, String build, String current) throws Exception {
        // HSQLDB writes next to its files even read-only: work on a copy.
        Path copy = Files.createTempDirectory("fakecar-kis");
        File[] files = kisFolder.listFiles((d, n) -> n.startsWith("KIS."));
        if (files == null || files.length == 0) throw new IllegalStateException("no KIS in " + kisFolder);
        for (File f : files) Files.copy(f.toPath(), copy.resolve(f.getName()), StandardCopyOption.REPLACE_EXISTING);
        Class.forName("org.hsqldb.jdbc.JDBCDriver");
        List<String> lines = new ArrayList<>();
        try (Connection c = DriverManager.getConnection("jdbc:hsqldb:file:" + copy.resolve("KIS") + ";readonly=true;ifexists=true", "SA", "")) {
            try (PreparedStatement p = c.prepareStatement("SELECT TYPMERKMALE FROM KIS.FAHRZEUGTYP WHERE NAME=?")) {
                p.setString(1, order.type);
                ResultSet r = p.executeQuery();
                if (!r.next()) throw new IllegalArgumentException("type key " + order.type + " is not in the " + kisFolder.getName() + " data");
                for (String kv : r.getString(1).split(";")) if (kv.contains("=")) order.features.put(kv.substring(0, kv.indexOf('=')), kv.substring(kv.indexOf('=') + 1));
            }
            long built = istep(c, build), now = istep(c, current);
            Map<Integer, String[]> parts = new LinkedHashMap<>();
            PreparedStatement lvs = c.prepareStatement(
                    "SELECT B.NAME, B.DIAGNOSEADRESSE, L.NAME, CAST(E.BEDINGUNG AS VARCHAR(16000000)) FROM KIS.LOGISTISCHEVERWENDUNG L"
                            + " JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID LEFT JOIN KIS.EINSATZBEDINGUNG E ON E.ID=L.EINSATZBEDINGUNG_ID"
                            + " WHERE L.TEILCHARAKTER='BVI' AND L.ISTUFE_ID=? ORDER BY B.DIAGNOSEADRESSE, B.NAME, L.ID DESC");
            lvs.setLong(1, built);
            ResultSet r = lvs.executeQuery();
            Set<String> done = new HashSet<>();
            while (r.next()) {
                String bntn = r.getString(1);
                if (done.contains(bntn) || parts.containsKey(r.getInt(2))) continue;
                String cond = r.getString(4);
                if (cond != null && !new Condition(cond, order).holds()) continue;
                done.add(bntn);
                parts.put(r.getInt(2), new String[] {bntn, r.getString(3)});
            }
            for (Map.Entry<Integer, String[]> e : parts.entrySet()) {
                String line = software(c, e.getKey(), e.getValue()[0], e.getValue()[1], now);
                if (line != null) lines.add(line);
            }
        } finally {
            deleteTree(copy.toFile());
        }
        return lines;
    }

    static long istep(Connection c, String name) throws Exception {
        try (PreparedStatement p = c.prepareStatement("SELECT ID FROM KIS.ISTUFE WHERE NAME=?")) {
            p.setString(1, name);
            ResultSet r = p.executeQuery();
            if (!r.next()) throw new IllegalArgumentException("I-step " + name + " is not in this PSdZData");
            return r.getLong(1);
        }
    }

    static final Map<Integer, String> PC = new HashMap<>();
    static {
        PC.put(1, "HWEL"); PC.put(2, "HWAP"); PC.put(3, "HWFR"); PC.put(4, "GWTB"); PC.put(5, "CAFD"); PC.put(6, "BTLD");
        PC.put(7, "FLSL"); PC.put(8, "SWFL"); PC.put(9, "SWFF"); PC.put(12, "IBAD"); PC.put(16, "FCFA"); PC.put(26, "TLRT");
    }

    /** addr, module, part, then its software at the current I-step (newest per unit) and the CAFDs of that software. */
    static String software(Connection c, int addr, String bntn, String part, long now) throws Exception {
        PreparedStatement lvs = c.prepareStatement(
                "SELECT L.ID FROM KIS.LOGISTISCHEVERWENDUNG L JOIN KIS.BORDNETZTEILNEHMER B ON B.ID=L.BNTN_ID"
                        + " WHERE B.NAME=? AND L.NAME=? AND L.TEILCHARAKTER='BVI' AND L.ISTUFE_ID<=? ORDER BY L.ISTUFE_ID DESC, L.ID DESC");
        lvs.setString(1, bntn);
        lvs.setString(2, part);
        lvs.setLong(3, now);
        ResultSet lr = lvs.executeQuery();
        while (lr.next()) {
            PreparedStatement tv = c.prepareStatement(
                    "SELECT T.ID, E.WERT, E.SGBM_NR, E.MAJOR, E.MINOR, E.PATCH FROM KIS.TECHNISCHEVERWENDUNG T"
                            + " JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=T.TE_ID WHERE T.LV_ID=? AND T.ISTUFE_ID<=? ORDER BY T.ISTUFE_ID, E.MAJOR, E.MINOR, E.PATCH");
            tv.setLong(1, lr.getLong(1));
            tv.setLong(2, now);
            ResultSet tr = tv.executeQuery();
            Map<String, String> units = new LinkedHashMap<>();
            Map<String, Long> unitTv = new LinkedHashMap<>();
            while (tr.next()) {
                String key = tr.getInt(2) + "/" + tr.getString(3).trim();
                units.put(key, id(tr.getInt(2), tr.getString(3), tr.getInt(4), tr.getInt(5), tr.getInt(6)));
                unitTv.put(key, tr.getLong(1));
            }
            if (units.isEmpty()) continue;
            List<String> ids = new ArrayList<>();
            for (int pc : new int[] {1, 2, 3, 6, 7, 8, 9, 12, 16, 4}) {
                for (Map.Entry<String, String> u : units.entrySet()) if (u.getKey().startsWith(pc + "/")) ids.add(u.getValue());
            }
            Map<String, String> cafds = new LinkedHashMap<>();
            for (Long unit : unitTv.values()) {
                PreparedStatement cp = c.prepareStatement(
                        "SELECT E.SGBM_NR, E.MAJOR, E.MINOR, E.PATCH FROM KIS.CAFMAPPING M JOIN KIS.TECHNISCHEEINHEIT E ON E.ID=M.CAF"
                                + " WHERE M.TV_SWE=? ORDER BY E.MAJOR, E.MINOR, E.PATCH");
                cp.setLong(1, unit);
                ResultSet cr = cp.executeQuery();
                while (cr.next()) cafds.put(cr.getString(1).trim(), id(5, cr.getString(1), cr.getInt(2), cr.getInt(3), cr.getInt(4)));
            }
            ids.addAll(cafds.values());
            return String.format("%02X", addr) + "\t" + bntn + "\t" + part + "\t" + String.join(" ", ids);
        }
        return null;
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
            return want.endsWith("*") ? have.startsWith(want.substring(0, want.length() - 1)) : have.equals(want);
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
