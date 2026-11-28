"""Protect the WinUI popup glass resources and accessibility fallback."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
ui = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
x = "{http://schemas.microsoft.com/winfx/2006/xaml}"
app = ET.parse(root / "windows/App.xaml").getroot()
dictionaries = app.find(f"{ui}Application.Resources/{ui}ResourceDictionary/{ui}ResourceDictionary.ThemeDictionaries")
assert dictionaries is not None
themes = {d.get(x + "Key"): {e.get(x + "Key"): e for e in d} for d in dictionaries}
backgrounds = ("ComboBoxDropDownBackground", "MenuFlyoutPresenterBackground", "FlyoutPresenterBackground")
highlights = ("ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed", "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedUnfocused", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "ToggleMenuFlyoutItemBackgroundPointerOver", "ToggleMenuFlyoutItemBackgroundPressed")
borders = ("ComboBoxDropDownBorderBrush", "MenuFlyoutPresenterBorderBrush")
for theme in ("Default", "Light"):
    resources = themes[theme]
    for key in backgrounds:
        brush = resources[key]
        assert brush.tag == ui + "AcrylicBrush" and 0 < float(brush.get("TintOpacity")) < 1, (theme, key)
    for key in highlights:
        brush = resources[key]
        assert brush.tag == ui + "SolidColorBrush" and int(brush.get("Color")[1:3], 16) < 128, (theme, key)
    for key in borders:
        stops = list(resources[key])
        assert resources[key].tag == ui + "LinearGradientBrush" and len(stops) == 2, (theme, key)
        assert int(stops[0].get("Color")[1:3], 16) > int(stops[1].get("Color")[1:3], 16), (theme, key)
for key in backgrounds + highlights + borders:
    brush = themes["HighContrast"][key]
    assert brush.tag == ui + "SolidColorBrush" and brush.get("Color").startswith("{ThemeResource SystemColor"), key
styles = [e for e in app.iter(ui + "Style") if e.get("TargetType") == "MenuFlyoutPresenter"]
assert len(styles) == 1 and styles[0].get("BasedOn") == "{StaticResource DefaultMenuFlyoutPresenterStyle}"
assert any(e.get("Property") == "SystemBackdrop" and e.get("Value") == "{x:Null}" for e in styles[0])
assert all(e.get("Property") not in ("Opacity", "Width", "Height", "Padding", "CornerRadius", "Template") for e in styles[0])
print("Popup acrylic, translucent states, fine gradient rims, system high contrast and preserved menu layout verified.")
