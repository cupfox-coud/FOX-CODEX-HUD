using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using CodexTokenHud;

class ContextLayoutTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(Hud h, string name, object value) { typeof(Hud).GetField(name, Private).SetValue(h, value); }
    static object Call(Hud h, string name, params object[] args) { return typeof(Hud).GetMethod(name, Private).Invoke(h, args); }
    static object Fixture(string name, params object[] args) { return typeof(CornerHudTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
    static double Now { get { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; } }
    static Bitmap Frame(Hud h, double clock)
    {
        Set(h, "previewMotionClock", clock);
        var bitmap = new Bitmap(400, 722);
        using (var g = Graphics.FromImage(bitmap)) Call(h, "PaintCorner", g);
        return bitmap;
    }
    static int Changes(Bitmap a, Bitmap b, Rectangle r)
    { int n = 0; for (int y=r.Top;y<r.Bottom;y++) for (int x=r.Left;x<r.Right;x++) if(a.GetPixel(x,y)!=b.GetPixel(x,y)) n++; return n; }
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    [STAThread] static void Main(string[] args)
    {
        string root = args[0];
        if (args.Length > 2 && args[2] == "probe")
        {
            var live = Json.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-token-hud", "runtime", "metrics.json"));
            var known = new HashSet<string>(Json.List(live, "threads").Select(t => Json.Text(t, "title")));
            var duplicates = new HashSet<string>(Json.List(live,"threads").GroupBy(t=>Json.Text(t,"title")).Where(g=>g.Count()>1).Select(g=>g.Key));
            var view = Inspector.Follow(null, known, duplicates);
            Console.WriteLine("Foreground identified=" + (view != null) + "; known threads=" + known.Count);
            if (view != null)
            {
                var fingerprints = Inspector.VisibleFingerprints(view);
                Json.Write(Path.Combine(root,"build","visible-context-probe.json"),new Dictionary<string,object>{{"title",view.Title},{"hashes",fingerprints}});
                Console.WriteLine("Visible identity fingerprints="+fingerprints.Length);

            }
            using(var process=System.Diagnostics.Process.GetProcessById((int)Native.ProcessId(Native.GetForegroundWindow()))) Console.WriteLine("Foreground app="+process.ProcessName);
            foreach(var handle in Native.Windows().Take(2))
            {
                var target=Inspector.Read(handle,known);
                Console.WriteLine("Codex window primary="+target.Primary+"; title="+target.Title);
            }
            return;
        }
        Application.EnableVisualStyles();
        RenderMemoryGuard.TestSnapshot = new MemorySnapshot {CommitLimit=100UL<<30,CommitAvailable=20UL<<30,PhysicalAvailable=8UL<<30};
        var config = new RuntimeConfiguration {Folder=root,DataDirectory=Path.Combine(root,"build","context-layout-runtime"),
            CodexHome=Path.Combine(root,"build","empty-context-home"),Pythonw=args[1],CornerMode=true,ResetAlerts=false,CornerMotion="on"};
        using (var h = new Hud(config))
        {
            foreach (string name in new[]{"ticker","motionTicker","hoverTicker"}) ((Timer)typeof(Hud).GetField(name,Private).GetValue(h)).Stop();
            Set(h,"scale",1.0); Set(h,"manualBindingId","layout-fixture"); Set(h,"selectedTitle","示例会话");
            Set(h,"motionAllowed",true); Set(h,"nextMotionPreference",double.MaxValue);
            double now=Now;
            var speed=(Dictionary<string,object>)Fixture("SpeedFixture",now,235.0,.35);
            speed["scope"]="global";speed["average_30s"]=232.0;speed["generating"]=3;speed["tools"]=1;speed["complete"]=true;speed["peak_rate"]=265;
            var context=new Dictionary<string,object>{{"input_tokens",127200},{"window_tokens",258400},{"sample_at_ms",now},{"sample_model","gpt-6.1-terra"},{"stale_model",false},{"pending_compaction",false}};
            var metrics=new Dictionary<string,object>{{"model","gpt-6.1-terra"},{"stage","generating"},{"turn_active",true},{"last_event_at_ms",now},
                {"response_duration",Fixture("ResponseFixture",now)},{"context",context}};
            var quota=new Dictionary<string,object>{{"state","ok"},{"updated_at_ms",now},
                {"pace",new Dictionary<string,object>{{"method","official_balance_5m"},{"state","ready"},{"rate_pct_per_hour",6.4},{"span_seconds",300},{"sample_at_ms",now},{"reset_at",now/1000+5.75*86400}}},
                {"windows",new object[]{new Dictionary<string,object>{{"window_minutes",10080},{"remaining_percent",63},{"resets_at",now/1000+5.75*86400}}}},
                {"runway",new Dictionary<string,object>{{"today",Fixture("TodayFixture",now,8.4)}}}};
            var radar=new Dictionary<string,object>{{"state","ok"},{"fetched_at_ms",now},{"source_updated_at_ms",now},{"upcoming",null}};
            var data=new Dictionary<string,object>{{"title","示例会话"},{"thread_id","layout-fixture"},{"binding","ok"},{"binding_mode","manual"},{"updated_at_ms",now},
                {"source",new Dictionary<string,object>{{"state","ok"}}},{"global_speed",speed},{"metrics",metrics},{"account_quota",quota},{"reset_radar",radar},
                {"network_latency",new Dictionary<string,object>{{"state","ok"},{"updated_at_ms",now},
                    {"nodes",new object[]{Fixture("Node","ai",116,now),Fixture("Node","general",158,now)}}}}};
            Set(h,"data",data); Set(h,"previewMotionClock",10000.0); Call(h,"UpdateMetrics");
            var output=Path.Combine(root,"build","corner55-review");Directory.CreateDirectory(output);
            if (args.Length > 2 && args[2] == "showcase")
            {
                data["binding_mode"]="follow"; Set(h,"manualBindingId",null);
                var demoView=new ViewSnapshot{Handle=Native.GetDesktopWindow(),Primary=true,Title="示例会话"};
                Set(h,"cornerInspectedView",demoView); Set(h,"view",demoView); Set(h,"waitingForAutoFocus",false);
                Call(h,"UpdateMetrics");
                using(var a=Frame(h,10000)) {}
                using(var a=Frame(h,10500)) a.Save(Path.Combine(output,"normal.png"));
                radar["upcoming"]=new Dictionary<string,object>{{"id","demo-global"},{"kind","reset_scheduled"},{"reset_type","global"},
                    {"precision","deadline"},{"start_ms",now+3600000},{"end_ms",now+3600000}};
                Call(h,"UpdateMetrics");using(var a=Frame(h,11000)) a.Save(Path.Combine(output,"global.png"));
                Json.Object(radar,"upcoming")["id"]="demo-banked"; Json.Object(radar,"upcoming")["reset_type"]="banked";
                Call(h,"UpdateMetrics");using(var a=Frame(h,12000)) a.Save(Path.Combine(output,"banked.png"));
                radar["upcoming"]=null; Call(h,"UpdateMetrics");
                var frames=Path.Combine(output,"frames"); Directory.CreateDirectory(frames);
                for(int i=0;i<80;i++)
                {
                    double at=Now; double rate=i<25?42.0:i<50?132.0:235.0;
                    data["updated_at_ms"]=at; metrics["last_event_at_ms"]=at; context["sample_at_ms"]=at;
                    quota["updated_at_ms"]=at; Json.Object(quota,"pace")["sample_at_ms"]=at;
                    speed["rate"]=rate; speed["average_30s"]=rate*.88; Set(h,"previewMotionClock",20000.0+i*50);
                    Call(h,"UpdateMetrics");
                    using(var frame=Frame(h,20000.0+i*50)) frame.Save(Path.Combine(frames,i.ToString("000")+".png"));
                }
                Console.WriteLine("Synthetic showcase: "+output); return;
            }
            using(var measure= new Bitmap(100,50)) using(var graphics=Graphics.FromImage(measure))
            {
                var font=(Font)typeof(Hud).GetField("cornerMetricFont",Private).GetValue(h);
                var unitFont=(Font)typeof(Hud).GetField("cornerMicroFont",Private).GetValue(h);
                Check(new[]{"+100","−100","−<0.1","6000"}.All(value=>graphics.MeasureString(value,font,1000,StringFormat.GenericTypographic).Width+
                    graphics.MeasureString(value=="6000"?"%/h":"%",unitFont,1000,StringFormat.GenericTypographic).Width+3<=73),
                    "quota pair reserves inline units even for extreme and near-zero readings");
            }
            using(var a=Frame(h,10000)) a.Save(Path.Combine(output,"normal.png"));
            double quotaClock=10400;
            foreach(double amount in new[]{0.0,-16.0,-100.0,-.04})
            {
                Json.Object(quota,"runway")["today"]=Fixture("TodayFixture",now,amount);
                Json.Object(quota,"pace")["rate_pct_per_hour"]=amount==-100?6000.0:amount==-.04?.04:0.0;
                Call(h,"UpdateMetrics");
                using(var a=Frame(h,quotaClock)) {}
                using(var a=Frame(h,quotaClock+400)) a.Save(Path.Combine(output,"quota-"+amount.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
                quotaClock+=800;
            }
            Json.Object(quota,"runway")["today"]=Fixture("TodayFixture",now,8.4);
            Json.Object(quota,"pace")["rate_pct_per_hour"]=6.4;
            Call(h,"UpdateMetrics");
            using(var a=Frame(h,10400)) using(var b=Frame(h,10800))
                Check(Changes(a,b,new Rectangle(36,110,130,106))>100,"high-speed rainbow flows without altering the rate");
            foreach(double p in new[]{72.0,88.0})
            {
                context["input_tokens"]=258400*p/100; Set(h,"previewMotionClock",12000+p);Call(h,"UpdateMetrics");
                using(var first=Frame(h,12000+p)) using(var last=Frame(h,13600+p))
                {
                    Check(Changes(first,last,new Rectangle(28,322,344,76))>100,"context "+p+" warning has a localized light transition");
                    last.Save(Path.Combine(output,p==72?"prepare.png":"handoff.png"));
                }
            }
            Json.Object(quota,"runway")["today"]=Fixture("TodayFixture",now,-8.4);
            radar["upcoming"]=new Dictionary<string,object>{{"id","reset-fixture"},{"kind","reset_scheduled"},{"reset_type","global"},{"precision","deadline"},{"start_ms",now+3600000},{"end_ms",now+3600000}};
            Call(h,"UpdateMetrics");using(var a=Frame(h,15000)) a.Save(Path.Combine(output,"reset-and-deficit.png"));
            var todayDigits=(ReadoutTransition)typeof(Hud).GetField("todayDigits",Private).GetValue(h);
            Check(!todayDigits.Moving(15000),"positive-to-negative advice changes atomically with the warning label");
            speed["rate"]=345.0;Call(h,"UpdateMetrics");
            using(var a=Frame(h,15400)) {}
            using(var a=Frame(h,16000)) a.Save(Path.Combine(output,"above-range.png"));
            Check(((SpeedDisplay)typeof(Hud).GetField("speedView",Private).GetValue(h)).Value=="345","above-range rate is never clamped to 300");
            var surge=(SpeedSurge)typeof(Hud).GetField("speedSurge",Private).GetValue(h);
            surge.Started=16000;surge.Gain=60;
            using(var lit=Frame(h,16700))
            {
                lit.Save(Path.Combine(output,"soft-output-sweep.png"));
                surge.Started=-100000;
                using(var calm=Frame(h,16700))
                {
                    Check(Changes(lit,calm,new Rectangle(28,78,344,170))>1000,"acceleration retains visible soft light within output");
                    Check(Changes(lit,calm,new Rectangle(20,250,360,156))==0,"output acceleration cannot illuminate response or context below it");
                }
            }
            Set(h,"motionAllowed",false);
            using(var a=Frame(h,17000)) using(var b=Frame(h,19000))
                Check(Changes(a,b,new Rectangle(28,322,344,76))==0,"reduced motion leaves a static context warning");
            data["activity_fallback"]=new Dictionary<string,object>{{"title","Recent task"},{"thread_id","recent"},
                {"binding","ok"},{"binding_mode","activity"},{"updated_at_ms",now},{"metrics",metrics},{"source",data["source"]}};
            data["binding"]="ambiguous";data["binding_mode"]="auto";data["metrics"]=null;data["thread_id"]=null;
            Set(h,"manualBindingId",null);Call(h,"UpdateMetrics");
            Check(((ActivityDisplay)typeof(Hud).GetField("activity",Private).GetValue(h)).AverageMs.HasValue &&
                (bool)typeof(Hud).GetField("activityUsesFallback",Private).GetValue(h),"ambiguous chat retains a separately qualified recent-task response");
            Check(!((ContextDisplay)typeof(Hud).GetField("contextView",Private).GetValue(h)).Ready &&
                ((ContextAlertTracker)typeof(Hud).GetField("contextAlerts",Private).GetValue(h)).Level==0,
                "recent-task fallback cannot supply current context or trigger its alerts");
            var chatA=new ViewSnapshot{Handle=Native.GetDesktopWindow(),Primary=true,Title="Chat A",ProcessId=uint.MaxValue};
            var chatB=new ViewSnapshot{Handle=Native.GetDesktopWindow(),Primary=true,Title="Chat B",ProcessId=uint.MaxValue};
            Call(h,"UpdateView",chatA);Call(h,"UpdateView",chatB);
            var selection=Json.Read(Path.Combine(config.Runtime,"selection.json"));
            Check(Json.Text(selection,"title")=="Chat B" && Json.Text(selection,"binding_mode")=="follow",
                "switching chats follows the new title even when a different app owns foreground");
            Call(h,"UpdateMetrics");Call(h,"UpdateView",chatB);
            Check(Json.Text(Json.Read(Path.Combine(config.Runtime,"selection.json")),"title")=="Chat B",
                "background activity cannot replace the last viewed chat");
            h.Close();
            Console.WriteLine("Review images: "+output);
        }
    }
}
