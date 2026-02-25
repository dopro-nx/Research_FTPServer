using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace Research_FTPServer
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        private int port = 2121; // 임시 포트 (일반적으로는 21을 사용)

        // Passive 데이터 포트 범위
        // 스터디 필요 (방화벽을 오픈해야한다는 정보가 있음)
        private int portMin = 50000;
        private int portMax = 50100;

        private string rootDirectory = $@"{Environment.GetEnvironmentVariable("SystemDrive")}\FTPImage";



        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Console.WriteLine(rootDirectory);
        }
    }
}
