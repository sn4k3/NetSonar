using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiveChartsCore.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace NetSonar.Avalonia.Views.Fragments;

public partial class PingableServiceGraphFragment : UserControl
{
    public PingableServiceGraphFragment()
    {
        InitializeComponent();
    }
}