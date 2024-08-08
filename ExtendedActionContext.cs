using Flow.Launcher.Plugin;
using System.Windows.Input;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class ExtendedActionContext : ActionContext
    {
        public bool IsTKeyPressed { get; set; }

        public ExtendedActionContext(ActionContext context, bool isTKeyPressed)
        {
            SpecialKeyState = context.SpecialKeyState;
            IsTKeyPressed = isTKeyPressed;
        }
    }
}