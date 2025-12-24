Imports System.Reflection
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.IO
Imports Autodesk.Revit.UI
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.UI.Events
Imports System.Linq
Imports KKY_Tool_Revit.UI
Imports KKY_Tool_Revit.UI.Hub
'커밋 확인용'
Namespace KKY_Tool_Revit
    Public Class App
        Implements IExternalApplication

        Public Function OnStartup(a As UIControlledApplication) As Result Implements IExternalApplication.OnStartup
            Const TAB_NAME As String = "KKY Tools"
            Const PANEL_NAME As String = "Hub"

            ' 탭 생성(이미 있으면 무시)
            Try : a.CreateRibbonTab(TAB_NAME) : Catch : End Try

            ' 패널 찾기/생성
            Dim ribbonPanel As RibbonPanel = a.GetRibbonPanels(TAB_NAME).FirstOrDefault(Function(p) p.Name = PANEL_NAME)
            If ribbonPanel Is Nothing Then
                ribbonPanel = a.CreateRibbonPanel(TAB_NAME, PANEL_NAME)
            End If

            ' 허브 버튼
            Dim asmPath = Assembly.GetExecutingAssembly().Location
            Dim cmdFullName As String = GetType(DuplicateExport).FullName
            Dim pbd As New PushButtonData("KKY_Hub_Button", "KKY Hub", asmPath, cmdFullName)

            Dim btn = TryCast(ribbonPanel.AddItem(pbd), PushButton)
            If btn IsNot Nothing Then
                btn.ToolTip = "KKY Tool 허브 열기"
                'Dim resNames = String.Join(vbCrLf, Assembly.GetExecutingAssembly().GetManifestResourceNames())
                'TaskDialog.Show("RES", resNames)

                Dim smallImg = LoadPng("KKY_Tool_Revit.Resources.icons.hub_16.png")
                If smallImg Is Nothing Then smallImg = LoadPng("KKY_Tool_Revit.Resources.icons.hub_16.png")

                Dim largeImg = LoadPng("KKY_Tool_Revit.Resources.icons.hub_32.png")
                If largeImg Is Nothing Then largeImg = LoadPng("KKY_Tool_Revit.Resources.icons.hub_32.png")

                btn.Image = smallImg
                btn.LargeImage = largeImg
            End If

            ' 활성 문서/뷰 변경 이벤트 구독
            AddHandler a.ViewActivated, AddressOf OnViewActivated
            AddHandler a.ControlledApplication.DocumentOpened, AddressOf OnDocumentListChanged
            AddHandler a.ControlledApplication.DocumentClosed, AddressOf OnDocumentListChanged

            Return Result.Succeeded
        End Function

        Public Function OnShutdown(a As UIControlledApplication) As Result Implements IExternalApplication.OnShutdown
            Try
                RemoveHandler a.ViewActivated, AddressOf OnViewActivated
                RemoveHandler a.ControlledApplication.DocumentOpened, AddressOf OnDocumentListChanged
                RemoveHandler a.ControlledApplication.DocumentClosed, AddressOf OnDocumentListChanged
            Catch
            End Try
            Return Result.Succeeded
        End Function

        Private Sub OnViewActivated(sender As Object, e As ViewActivatedEventArgs)
            Try
                HubHostWindow.NotifyActiveDocumentChanged(e.Document)
            Catch
            End Try
        End Sub

        Private Sub OnDocumentListChanged(sender As Object, e As EventArgs)
            Try
                HubHostWindow.NotifyDocumentListChanged()
            Catch
            End Try
        End Sub

        Private Function LoadPng(resName As String) As ImageSource
            Dim asm = Assembly.GetExecutingAssembly()
            Dim resolvedName = ResolveResourceName(asm, resName)
            If String.IsNullOrEmpty(resolvedName) Then Return Nothing

            Using s = asm.GetManifestResourceStream(resolvedName)
                If s Is Nothing Then Return Nothing
                Dim decoder = New PngBitmapDecoder(
                    s,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad)
                Dim src = decoder.Frames(0)
                src.Freeze()
                Return src
            End Using
        End Function

        Private Function ResolveResourceName(asm As Assembly, desired As String) As String
            Dim names = asm.GetManifestResourceNames()

            Dim exact = names.FirstOrDefault(Function(n) String.Equals(n, desired, StringComparison.OrdinalIgnoreCase))
            If Not String.IsNullOrEmpty(exact) Then Return exact

            Dim fileName = Path.GetFileName(desired)
            Dim byFile = names.FirstOrDefault(Function(n) n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            If Not String.IsNullOrEmpty(byFile) Then Return byFile

            Return Nothing
        End Function
    End Class
End Namespace
