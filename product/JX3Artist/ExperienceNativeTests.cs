using System.IO;
using System.Windows;
namespace JX3Artist;
public partial class MainWindow
{
    void AddExperienceUiTests(Action<string,Action> test,string output)
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        test("experience card exposes only this session and lifetime without initialization",()=>{
            RefreshExperience();Check(FindName("ExperienceInitButton")==null&&FindName("ExperienceBaselineNote")==null,"retired input still present");
            Check(ExperienceGain.Text.StartsWith("本次 +")&&ExperienceTotal.Text.StartsWith("累计 +"),"summary labels missing");
            Check(ExperienceHint.Visibility==Visibility.Collapsed,"explanatory text on main card");
        });
        test("experience summary includes current session in lifetime and keeps it across stop",()=>{
            long start=experience.OpenedAt;
            Observation Panel(long at)=>new(at,at+100,true,true,"NO_VERIFIED_CANDIDATE",0,"VISIBLE",PanelLocated:true,PanelLocation:"UNIQUE_PANEL");
            experience.Observe(Panel(start),Phase.Performing,0,0,120000);experience.Observe(Panel(start+60000),Phase.Performing,0,0,120000);Refresh();
            Check(ExperienceGain.Text=="本次 +6"&&ExperienceTotal.Text=="累计 +6","totals differ");
            DispatchHotkey(AssistantHotkeys.StopId);RefreshExperience();Check(ExperienceGain.Text=="本次 +6"&&ExperienceTotal.Text=="累计 +6","stop reset totals");
            SaveUi(Path.Combine(output,"experience-summary.png"));
        });
        test("experience two-number summary remains inside the minimum window",()=>{
            double width=Width,height=Height;try{Width=900;Height=650;UpdateLayout();
                foreach(var control in new FrameworkElement[]{ExperienceGain,ExperienceTotal}){var point=control.TranslatePoint(new Point(),this);Check(point.X>=0&&point.Y>=0&&point.X+control.ActualWidth<=ActualWidth&&point.Y+control.ActualHeight<=ActualHeight,"experience clipped");}
            }finally{Width=width;Height=height;}
        });
    }
}
