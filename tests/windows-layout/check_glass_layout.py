"""Check that glass decoration never defines the size of auto-height content."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
xml = ET.parse(root / "windows/MainWindow.xaml").getroot()
ui = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
x = "{http://schemas.microsoft.com/winfx/2006/xaml}"
resources = xml.find(f"{ui}Grid/{ui}Grid.Resources")
assert resources is not None
style = next(node for node in resources if node.get(x + "Key") == "HarborPanel")
template = next(node for node in style.iter(ui + "ControlTemplate"))
grid = next(iter(template))
children = list(grid)
assert len(children) == 2 and children[0].tag == ui + "Canvas" and children[1].tag == ui + "ContentPresenter", \
    "Decorative glass must be measured by Canvas, not impose a size alongside the content."
assert children[0].get(x + "Name") == "DecorationLayer"
parts = {node.get(x + "Name") for node in children[0]}
assert {"ShadowImage", "Surface", "Film", "Highlight", "Stroke"}.issubset(parts)
assert children[1].get("Padding") == "{TemplateBinding Padding}"
by_name = {node.get(x + "Name"): node for node in xml.iter() if node.get(x + "Name")}
assert by_name["ThemeScopeRows"].get("Visibility") == "Collapsed"
assert by_name["ThemeCustomColorButton"].get("Grid.Column") == "1"
palette_parent = next(node for node in xml.iter() if by_name["ThemePalette"] in list(node))
assert palette_parent.get("ColumnDefinitions") == "*,Auto", "Color picker must sit at the right edge of the palette card."
assert by_name["AccountListCard"].get("CornerRadius") == "24"
assert by_name["AccountDetailsCard"].get("CornerRadius") == "26"
assert by_name["FeaturedGameCard"].get("CornerRadius") == "28"
assert by_name["WorkspaceSurface"].get("CornerRadius") == "28"
for name in ("SettingsPalette", "SettingsThemeScopes", "SettingsCollectionDisplay", "SettingsGlass", "SettingsWallpaper", "SettingsSounds", "SettingsDependencies", "SettingsGameOptions"):
    assert by_name[name].get("CornerRadius") == "24" and by_name[name].get("Padding") == "24", name
border = next(node for node in style if node.get("Property") == "BorderThickness")
assert border.get("Value") == "0.7", "Default glass border should match the Mac material's 0.7-point stroke."
runtime = by_name["RuntimeSettings"]
order = [node.get(x + "Name") for node in runtime]
assert order.index("SettingsPaths") < order.index("SettingsLauncher") < order.index("SettingsDependencies") < order.index("SettingsGameOptions"), order
disclosure = next(node for node in by_name["SettingsThemeScopes"].iter(ui + "Button") if node.get("Click") == "ThemeScopeToggle_Click")
assert disclosure.get("Style") == "{StaticResource HarborDisclosureHeader}"
print("Glass measurement, Mac card radii and stroke, settings order, disclosure styling and palette alignment verified.")
