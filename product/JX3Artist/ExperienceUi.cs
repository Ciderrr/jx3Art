using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace JX3Artist;
internal static class ExperienceUi
{
    internal static ExperienceBaseline Parse(string level,string experience)
    {
        if(!int.TryParse(level.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int l)||l<=0||!long.TryParse(experience.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out long x)||x<0)
            throw new ArgumentException("请填写正整数等级和非负整数经验");
        return new(l,x,0);
    }
}
public partial class MainWindow
{
    ExperienceEstimateView? renderedExperience;long? renderedLifetime;bool renderedLifetimePartial;int renderedExperienceSaveErrors=-1;string? renderedExperienceSaveError;
    void RefreshExperience()
    {
        var value=experience.Read();
        long? total=experience.LifetimeGain;bool partial=experience.LifetimePartial;int failures=experience.SaveErrors;string? error=experience.SaveError;
        ExperienceHint.Foreground=(System.Windows.Media.Brush)Resources[error is null?"Muted":"StopInk"];
        if(renderedExperience==value&&renderedLifetime==total&&renderedLifetimePartial==partial&&renderedExperienceSaveErrors==failures&&renderedExperienceSaveError==error)return;
        renderedExperience=value;renderedLifetime=total;renderedLifetimePartial=partial;renderedExperienceSaveErrors=failures;renderedExperienceSaveError=error;
        ExperienceGain.Text=$"本次 +{value.EstimatedGain:N0}";
        ExperienceTotal.Text=total.HasValue?$"累计 {(partial?"≈":"+")}{total.Value:N0}":"累计 —";
        ExperienceHint.Text=error??"";ExperienceHint.Visibility=error is null?Visibility.Collapsed:Visibility.Visible;
        ExperienceHistoryNote.Text=(experience.Previous is ExperienceEntry previous?$"上次估算 +{previous.Estimate.EstimatedGain:N0}{(previous.Complete?"":"（未正常关闭）")}":"暂无上次经验记录")+(partial?"\n早期记录不全，累计仅含可恢复部分":"")+(!total.HasValue?"\n累计暂无法确认":"");
        ExperienceHistoryNote.ToolTip="按观察到的连续表演时段估算；未知时段不补算。累计包含本次，历史详情保留最近10次。";
        service.History.Record("experience_status",new{ExperienceEstimatedGain=value.EstimatedGain,ExperienceLifetimeGain=total,ExperienceLifetimePartial=partial,ExperienceObservedMs=value.ObservedMilliseconds,ExperienceHasUnobservedTime=value.HasUnobservedTime,ExperienceSaveErrors=failures,ExperienceSaveFailure=error!=null});
    }
}
