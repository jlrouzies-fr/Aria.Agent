namespace Aria.Web.Helpers;

// Warhammer 40K pixel-art avatar sprites — face/bust portraits, 16×16.
// 0=transparent  1=dark outline/shadow  2=body/armor  3=bright highlight/lens
public static partial class AgentSprites
{
    private static readonly Dictionary<string, string> Data = new()
    {
        // ── Space Marine ─────────────────────────────────────────────────────
        // Dome helmet, twin glowing eye lenses, T-visor band, gorget collar
        ["space-marine"] =
            "0011111111100000" +   // dome top
            "0122222222210000" +   // dome
            "0122222222210000" +
            "0121111111210000" +   // brow/visor band (all dark)
            "0121331331210000" +   // twin lenses  (3=glow, 1=socket)
            "0121331331210000" +
            "0121111111210000" +   // lower visor
            "0122222222210000" +   // cheek plate
            "0122122112210000" +   // mouth grille (1=slits)
            "0122222222210000" +   // chin guard
            "0012222222100000" +
            "0112222222110000" +   // gorget start
            "1222222222221000" +   // collar
            "1222222222221000" +
            "1222222222221000" +
            "1122222222211000",

        // ── Chaos Space Marine ───────────────────────────────────────────────
        // Central spike tip above dome, asymmetric eyes (left lit, right damaged-dark),
        // chaos glyph on cheek, jagged trim on collar
        ["chaos-marine"] =
            "0000033000000000" +   // spike tip (3,3)
            "0012233222210000" +   // spike base merges into dome
            "0122222222210000" +
            "0121111111210000" +   // visor band
            "0121331111210000" +   // left eye lit, right dead-dark
            "0121331111210000" +
            "0121111111210000" +
            "0122232222210000" +   // chaos glyph on cheek
            "0122222222210000" +
            "0122222222210000" +
            "0012222222100000" +
            "0112222222110000" +
            "1222232222221000" +   // chaos trim on collar
            "1222222222221000" +
            "1222222222221000" +
            "1122222222211000",

        // ── Tech-Priest / Magos ──────────────────────────────────────────────
        // Hood (narrower top), cog highlights on forehead, THREE bionic eyes,
        // solid respirator mask on lower face, widening robe collar
        ["tech-priest"] =
            "0001111111110000" +   // hood top
            "0012222222210000" +
            "0012222222210000" +
            "0012223322210000" +   // forehead cog highlights (3,3)
            "0012313131210000" +   // three bionic eyes (3=eye, 1=gap)
            "0012222222210000" +
            "0012111111210000" +   // respirator (all-dark lower face)
            "0012111111210000" +
            "0012122221210000" +   // respirator detail
            "0012222222210000" +
            "0012222222210000" +
            "0012222222210000" +
            "0112222222110000" +
            "1222222222221000" +   // robe collar widens
            "1222222222221000" +
            "1122222222211000",

        // ── Inquisitor ───────────────────────────────────────────────────────
        // Full-width hat brim row 0 (signature silhouette), face narrower below,
        // inquisitive close-set eyes, stern mouth, long coat collar
        ["inquisitor"] =
            "0111111111111100" +   // wide hat brim (nearly full width)
            "0011222222110000" +   // hat crown
            "0001222221000000" +   // face top (narrower than brim)
            "0001222221000000" +
            "0001232321000000" +   // eyes (cols 5,7 = 3)
            "0001222221000000" +
            "0001222221000000" +
            "0001222221000000" +
            "0001211221000000" +   // stern set mouth
            "0001222221000000" +
            "0001222221000000" +
            "0001222221000000" +
            "0012222222100000" +   // collar
            "0122222222210000" +
            "0122222222210000" +
            "0112222222211000",

        // ── Commissar ────────────────────────────────────────────────────────
        // Pointed peaked cap (narrow tip, sloped brim), cold eyes, high collar
        ["commissar"] =
            "0000011100000000" +   // peaked cap tip (narrow)
            "0001122211000000" +   // cap crown
            "0012222222100000" +   // cap crown wider
            "1122222222110000" +   // cap brim (overhangs face)
            "0012222222100000" +   // face
            "0012232322100000" +   // cold eyes (cols 5,7 = 3)
            "0012222222100000" +
            "0012222222100000" +
            "0012211222100000" +   // tight closed mouth
            "0012222222100000" +
            "0012222222100000" +
            "0012222222100000" +
            "0112222222110000" +   // high collar
            "1222222222221000" +
            "1222222222221000" +
            "1122222222211000",

        // ── Imperial Guardsman ───────────────────────────────────────────────
        // Round helmet, single horizontal visor slot with small eyes, simple build
        ["guardsman"] =
            "0001111111000000" +   // round dome
            "0012222221000000" +
            "0012222221000000" +
            "0012111121000000" +   // visor slot (all dark)
            "0012133121000000" +   // eyes in visor (cols 5,6 = 3,3)
            "0012111121000000" +   // visor lower edge
            "0012222221000000" +   // cheek
            "0012222221000000" +
            "0012222221000000" +
            "0012212221000000" +   // neutral mouth
            "0012222221000000" +
            "0001222210000000" +   // chin (narrower)
            "0001222210000000" +
            "0012222221000000" +
            "0012222222100000" +
            "0011222211100000",

        // ── Sister of Battle ─────────────────────────────────────────────────
        // Pointed headdress/veil above helm (3=bright spire tips), wide bright eyes,
        // purity-seal gorget
        ["sister"] =
            "0001313310000000" +   // headdress spire tips (3=bright)
            "0001222210000000" +   // headdress
            "0001222210000000" +
            "0012222221000000" +   // face (wider at face level)
            "0012333221000000" +   // bright wide eyes (cols 4-6 = 3,3,3)
            "0012222221000000" +
            "0012222221000000" +
            "0012222221000000" +
            "0012213221000000" +   // slight determined mouth
            "0012222221000000" +
            "0012222221000000" +
            "0012222221000000" +
            "0012222221000000" +
            "0122222222210000" +   // pauldron/gorget collar
            "0122222222210000" +
            "0112222222211000",

        // ── Skitarii Ranger ──────────────────────────────────────────────────
        // Extremely wide hat brim row 1 (full width), bionic eye array (3 bright),
        // solid respirator/lower face dark, long coat collar
        ["skitarii"] =
            "0000000000000000" +   // empty — hat pushes face down
            "1111111111111110" +   // hat brim (full 15-wide!)
            "0012222222210000" +   // hat crown
            "0001222221000000" +   // face
            "0001233321000000" +   // optical array (3,3,3 = triple lens)
            "0001222221000000" +
            "0001111221000000" +   // respirator (dark lower face)
            "0001111221000000" +
            "0001222221000000" +
            "0001222221000000" +
            "0001222221000000" +
            "0001222221000000" +
            "0012222222100000" +
            "0122222222210000" +
            "0122222222210000" +
            "0112222222211000",

        // ── Navigator ────────────────────────────────────────────────────────
        // High noble collar peak, single third eye in forehead center (rows 3-4),
        // two normal eyes lower (row 5), flowing robe collar
        ["navigator"] =
            "0001111111100000" +   // high collar peak
            "0012222222100000" +
            "0012222222100000" +   // forehead
            "0012223222100000" +   // THIRD EYE (col 6 = 3)
            "0012223222100000" +   // third eye cont (2 rows tall)
            "0012322232100000" +   // normal eyes (cols 4,8 = 3)
            "0012222222100000" +
            "0012222222100000" +
            "0012222222100000" +
            "0012212222100000" +   // mouth
            "0012222222100000" +
            "0012222222100000" +
            "0112222222110000" +   // high noble collar
            "0122222222210000" +
            "0122222222210000" +
            "0112222222211000",
    };
}
