#!/usr/bin/env python3
"""Rewrites the KoLibri desktop shell from sidebar navigation to a
KoLmafia-style menu bar.

The old shell had a 260px sidebar on the left with the sections stacked
vertically and a compact variant for narrow windows. KoLmafia puts the
sections in a menu bar at the top, which is what the operator expects
from a KoL client. This script swaps that in and keeps the content area
and the status bar untouched.
"""

import re
import sys
from pathlib import Path

AXAML = Path("KoLibri.Desktop/MainWindow.axaml")

MENU_BAR = '''  <Grid x:Name="ShellRoot"
        RowDefinitions="Auto,*,Auto"
        ColumnDefinitions="*"
        Background="{DynamicResource CanvasBrush}">

    <!-- KoLmafia-style menu bar: the sections live in a row at the top,
         the way a KoL player expects them. -->
    <Border x:Name="MenuBar"
            Grid.Row="0"
            Classes="shell-header"
            BorderThickness="0,0,0,1"
            Padding="14,8">
      <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="18">
        <StackPanel Orientation="Horizontal" Spacing="2">
          <TextBlock Text="KoLibri"
                     FontSize="17"
                     FontWeight="Bold"
                     Foreground="{DynamicResource InkBrush}"
                     VerticalAlignment="Center"
                     Margin="0,0,16,0" />
          <Button x:Name="NavOverview"
                  Classes="menu-item"
                  Content="Guts"
                  Tag="Übersicht"
                  Click="OverviewClick"
                  AutomationProperties.Name="Guts öffnen" />
          <Button x:Name="NavInventory"
                  Classes="menu-item"
                  Content="Inventar"
                  Tag="Inventar"
                  Click="InventoryClick"
                  AutomationProperties.Name="Inventar öffnen" />
          <Button x:Name="NavDaily"
                  Classes="menu-item"
                  Content="Täglich"
                  Tag="Täglich"
                  Click="DailyClick"
                  AutomationProperties.Name="Tägliche Routine öffnen" />
          <Button x:Name="NavPricing"
                  Classes="menu-item"
                  Content="Pricing"
                  Tag="Pricing Engine"
                  Click="PricingClick"
                  AutomationProperties.Name="Pricing Engine öffnen" />
          <Button x:Name="NavMall"
                  Classes="menu-item"
                  Content="Mall"
                  Tag="Mall &amp; Shop"
                  Click="MallClick"
                  AutomationProperties.Name="Mall und Shop öffnen" />
          <Button x:Name="NavAdventure"
                  Classes="menu-item"
                  Content="Abenteuer"
                  Tag="Adventure"
                  Click="AdventureClick"
                  AutomationProperties.Name="Adventure öffnen" />
          <Button x:Name="NavCombat"
                  Classes="menu-item"
                  Content="Combat"
                  Tag="Combat"
                  Click="CombatClick"
                  AutomationProperties.Name="Combat öffnen" />
        </StackPanel>

        <StackPanel Grid.Column="1" />

        <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8">
          <StackPanel Spacing="2" VerticalAlignment="Center">
            <TextBlock Text="Konto" Classes="muted" FontSize="10" />
            <ComboBox x:Name="AccountRoleSelector"
                      Width="112"
                      SelectedIndex="1"
                      SelectionChanged="AccountRoleSelectionChanged"
                      AutomationProperties.Name="Aktives Konto auswählen">
              <ComboBoxItem Content="Main" />
              <ComboBoxItem Content="Mallbot" />
            </ComboBox>
          </StackPanel>
          <Button x:Name="GlobalRefreshButton"
                  Classes="secondary"
                  Content="⟳"
                  Click="RefreshClick"
                  AutomationProperties.Name="Daten aktualisieren" />
          <Button x:Name="ThemeToggleButton"
                  Classes="secondary"
                  Content="☾"
                  Click="ThemeToggleClick"
                  AutomationProperties.Name="Theme wechseln" />
        </StackPanel>
      </Grid>
    </Border>

'''

CONTENT_HEAD = '''    <ScrollViewer Grid.Row="1"
                  HorizontalScrollBarVisibility="Disabled"
                  VerticalScrollBarVisibility="Auto">
      <StackPanel x:Name="ContentRoot"
                  Margin="{DynamicResource ComfortableContentMargin}"
                  Spacing="24">
'''


def main() -> int:
    text = AXAML.read_text(encoding="utf-8")

    # 1. Replace the shell root + sidebar + compact navigation with the
    #    menu bar. The block runs from the <Grid x:Name="ShellRoot"> opening
    #    tag up to the <ScrollViewer that holds the content.
    start = text.index('  <Grid x:Name="ShellRoot"')
    end = text.index('    <ScrollViewer Grid.Column="1"')
    text = text[:start] + MENU_BAR + CONTENT_HEAD + text[end + len('    <ScrollViewer Grid.Column="1"\n                  Grid.Row="1"\n                  HorizontalScrollBarVisibility="Disabled"\n                  VerticalScrollBarVisibility="Auto">\n      <StackPanel x:Name="ContentRoot"\n                  Margin="{DynamicResource ComfortableContentMargin}"\n                  Spacing="24">\n'):]

    AXAML.write_text(text, encoding="utf-8")
    print(f"rewrote {AXAML} ({len(text)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
