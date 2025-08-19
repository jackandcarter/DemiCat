using System.Collections.Generic;

namespace DemiCatPlugin;

public class SignupPreset
{
    public string Name { get; set; } = string.Empty;
    public List<Template.TemplateButton> Buttons { get; set; } = new();
}
