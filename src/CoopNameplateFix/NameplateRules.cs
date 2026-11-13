using System;
using System.Globalization;

// Pure rules of the CoopNameplateFix part (PLAN-ALPHA2.md issue 8, unit U4; owner decision 6: the nickname above the
// partner about half the current size, slightly more transparent, colour kept). No game types: compiled with the part
// and, alone, with checks\NameplateChecks.cs by Test-Managed.ps1 (and its mutants by mutants\Test-Mutants.ps1).
//
// What the base mod draws (release IL, DeadCellsMultiplayerMod 0.5.1 378BE6B0):
//   GhostHero.SetLabel (IL 57257-57440): Assets.makeText(name, COLORS["ST"], null, entity.spr) - the label is a dc.ui.Text
//     child of the player's world sprite - then y -= 80, x -= 2.5*len, alpha = 0.8, customScale = GetNicknameScale(),
//     onResize(), textColor = COLORS["ST"]. Alpha is written there only. The entities are the partner's avatar
//     (ReceiveGhostCoords IL 37686, EnsureClientKingSlot IL 38384, CreateGhostKing IL 56870) and the local hero itself
//     (hook_level_changed IL 34768, EnsureCoopRuntimeBootstrap IL 35379: SetLabel(ModEntry.me, Username)): every nickname
//     label in GhostHero._labels gets the same treatment.
//   GhostHero.UpdateLabels (IL 57441-57554), every frame from ModEntry.hook_boot_update (IL 34166-34179): customScale =
//     GetNicknameScale(); onResize(); dir<0: scaleX = -scaleX and x = +2.5*len, else x = -2.5*len; y = -80.
//   GhostHero.GetNicknameScale (IL 57555-57602): 0.5 fullscreen/borderless, 0.8 windowed (ResolveNicknameScale, cached).
// What the native text does with it (hlboot A37869A3): ui.Text.onResize f@3413 picks a font and a factor from the UI
//   pixelScale only and sets scaleX = scaleY = factor*customScale (Text.hx:387), so the drawn size is linear in
//   customScale. h2d.Text.getBoundsRec f@3393 (forSize=false) draws the text box [xMin, xMin+w] x [calcYMin, calcHeight]
//   with xMin = realMaxWidth>=0 ? 0 : calcXMin and w = realMaxWidth>=0 ? realMaxWidth : calcWidth (get_textWidth f@3389,
//   get_textHeight f@3388 return calcWidth / calcHeight after initGlyphs).
// The fix: GetNicknameScale's result times Scale (default 0.5), and after UpdateLabels: the text box centred on the
// sprite origin (mirrored as the base mirrors for dir<0), its bottom edge kept where the base mod's full-size box ended,
// alpha Alpha (default 0.65). textColor is never written (the CoopLobby colour stays).
public static class NameplateRules
{
    public const string DisableSwitch="COOP_NAMEPLATE_DISABLE";
    public const string ScaleSwitch="COOP_NAMEPLATE_SCALE";
    public const string AlphaSwitch="COOP_NAMEPLATE_ALPHA";
    public const double DefaultScale=0.5;     // owner decision 6: about half the current size
    public const double DefaultAlpha=0.65;    // base 0.8; owner decision 6: slightly more transparent
    public const double MinFactor=0.2;        // PLAN-ALPHA2.md issue 8: clamped to 0.2-1
    public const double MaxFactor=1.0;
    public const double BaseY=-80;            // UpdateLabels IL_008F ldc.i4.s -80 (SetLabel IL_017B y -= 80)
    public const double MaxTextExtent=65536;  // a text box beyond this is not a nickname: keep the base layout

    public static bool Disabled(string value)=>value=="1";

    // An override value: unset -> fallback (note null); a finite invariant-culture number -> clamped to [MinFactor,
    // MaxFactor] (note when clamped); anything else -> fallback with a note. Never throws.
    public static double Factor(string raw,double fallback,out string note)
    {
        note=null;
        if(raw==null)return fallback;
        var text=raw.Trim();
        if(text.Length==0)return fallback;
        if(!double.TryParse(text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))
        {
            note="'"+raw+"' ignored (expects a number "+MinFactor.ToString(CultureInfo.InvariantCulture)+".."+MaxFactor.ToString(CultureInfo.InvariantCulture)+")";
            return fallback;
        }
        if(value<MinFactor){note=text+" clamped to "+MinFactor.ToString(CultureInfo.InvariantCulture);return MinFactor;}
        if(value>MaxFactor){note=text+" clamped to "+MaxFactor.ToString(CultureInfo.InvariantCulture);return MaxFactor;}
        return value;
    }

    // GetNicknameScale postfix: the base value times k. A base value that is not a positive finite number, or a k
    // outside (0,1], is returned unchanged (the base mod's own result wins).
    public static double Nickname(double baseScale,double k)
    {
        if(!double.IsFinite(baseScale)||baseScale<=0||!ValidK(k))return baseScale;
        return baseScale*k;
    }
    static bool ValidK(double k)=>double.IsFinite(k)&&k>0&&k<=MaxFactor;

    // The drawn text box of the label in its own coordinates (getBoundsRec f@3393, forSize=false).
    public static void Box(int calcXMin,int calcWidth,double realMaxWidth,out double xMin,out double width)
    {
        if(realMaxWidth>=0){xMin=0;width=realMaxWidth;}
        else{xMin=calcXMin;width=calcWidth;}
    }

    public struct Layout
    {
        public bool Ok;
        public double X,Y;
    }
    // Position of the label in its player sprite's coordinates, after the base mod's UpdateLabels set scaleX/scaleY
    // (onResize; scaleX negated when dir<0). The box centre lands on the sprite origin for either dir: the sprite is
    // mirrored for dir<0 and the label's own scaleX is negated, so the parent-space box is [x+s*xMin, x+s*(xMin+w)]
    // (dir>=0) or [x-s*(xMin+w), x-s*xMin] (dir<0). The box bottom (calcHeight) stays where the base mod's box ended
    // (its scale was scaleY/k): y + sy*bottom == BaseY + sy*bottom/k. Ok=false: keep the base mod's values.
    public static Layout Place(double xMin,double width,double bottom,double scaleX,double scaleY,int dir,double k)
    {
        double s=Math.Abs(scaleX),sy=Math.Abs(scaleY);
        if(!double.IsFinite(xMin)||!double.IsFinite(width)||!double.IsFinite(bottom)||!double.IsFinite(s)||!double.IsFinite(sy)||
            s<=0||sy<=0||width<0||bottom<0||!ValidK(k)||
            Math.Abs(xMin)>MaxTextExtent||width>MaxTextExtent||bottom>MaxTextExtent)
            return new Layout{Ok=false};
        double half=s*(xMin+width/2);
        return new Layout{Ok=true,X=dir<0?half:-half,Y=BaseY+sy*bottom*(1/k-1)};
    }
}

// Per-part fault gate (thresholds of the pack's PartGate / LobbyCore.FaultGate / OptFaultGate): trips at 8 consecutive
// or 64 total faults. Fail returns what to log; the caller logs and stops patching when Tripped.
public sealed class NameplateFaultGate
{
    public const int ConsecutiveLimit=8;
    public const int TotalLimit=64;
    int _consecutive;long _total;bool _tripLogged;
    public bool Tripped{get;private set;}
    public long Faults=>_total;
    public void Ok(){_consecutive=0;}
    // first: this is the first fault (log it); trip: the gate tripped now (log it once).
    public void Fail(out bool first,out bool trip)
    {
        _total++;_consecutive++;
        first=_total==1;
        if(_consecutive>=ConsecutiveLimit||_total>=TotalLimit)Tripped=true;
        trip=Tripped&&!_tripLogged;
        if(trip)_tripLogged=true;
    }
}

// Rate limit of the per-label log line: at most one line per IntervalMs; the lines held back are counted and reported
// with the next allowed one.
public sealed class NameplateLogLimiter
{
    readonly long _intervalMs;long _next=long.MinValue;long _held;
    public NameplateLogLimiter(long intervalMs){_intervalMs=intervalMs;}
    public bool Allow(long nowMs,out long heldBack)
    {
        heldBack=0;
        if(_next!=long.MinValue&&nowMs<_next){_held++;return false;}
        heldBack=_held;_held=0;_next=nowMs+_intervalMs;
        return true;
    }
}
