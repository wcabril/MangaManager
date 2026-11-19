using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MangaManager
{
    // Painel de apoiadores: quem entra no mangas.animeunited.com.br.
    //
    // Toda operação exige admin no servidor — a janela só esconde o que o
    // servidor já recusaria. Selecionar uma linha preenche o formulário: salvar
    // então ATUALIZA aquele cadastro; com nada selecionado, salvar CRIA.
    public partial class UsersWindow : Window
    {
        private readonly AnimeUnitedClient _client;
        private readonly string _meuEmail;
        private List<RemoteUser> _usuarios = new();
        private RemoteUser? _selecionado;

        public UsersWindow(string apiUrl, string apiKey, string meuEmail)
        {
            InitializeComponent();
            _client = new AnimeUnitedClient(apiUrl, apiKey, meuEmail);
            _meuEmail = meuEmail ?? "";
            Loaded += async (_, _) => await CarregarAsync();
        }

        private void Status(string texto, bool erro = false)
        {
            TxtStatus.Text = texto;
            TxtStatus.Foreground = erro
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        }

        private async System.Threading.Tasks.Task CarregarAsync()
        {
            Status("Carregando...");
            try
            {
                _usuarios = await _client.GetUsersAsync();
                GridUsuarios.ItemsSource = _usuarios;

                int comAcesso = _usuarios.Count(u => u.AccessLevel is "supporter" or "admin");
                TxtResumo.Text = $"{_usuarios.Count} cadastro(s) · {comAcesso} com acesso";
                Status("");
            }
            catch (Exception ex)
            {
                Status(ex.Message, erro: true);
            }
        }

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selecionado = GridUsuarios.SelectedItem as RemoteUser;
            if (_selecionado == null) return;

            TxtNome.Text = _selecionado.Name;
            TxtEmail.Text = _selecionado.Email;
            foreach (ComboBoxItem item in CboNivel.Items)
                if ((item.Content as string) == _selecionado.AccessLevel) CboNivel.SelectedItem = item;

            BtnSalvar.Content = "Salvar alterações";
        }

        private void BtnNovo_Click(object sender, RoutedEventArgs e)
        {
            GridUsuarios.SelectedItem = null;
            _selecionado = null;
            TxtNome.Text = "";
            TxtEmail.Text = "";
            CboNivel.SelectedIndex = 1;      // supporter
            BtnSalvar.Content = "Adicionar";
            TxtNome.Focus();
            Status("");
        }

        private async void BtnSalvar_Click(object sender, RoutedEventArgs e)
        {
            string nome  = TxtNome.Text.Trim();
            string email = TxtEmail.Text.Trim();
            string nivel = (CboNivel.SelectedItem as ComboBoxItem)?.Content as string ?? "none";

            if (nome.Length == 0 || email.Length == 0)
            {
                Status("Preencha nome e email.", erro: true);
                return;
            }

            BtnSalvar.IsEnabled = false;
            try
            {
                if (_selecionado == null)
                {
                    await _client.CreateUserAsync(nome, email, nivel);
                    Status($"{nome} adicionado.");
                }
                else
                {
                    await _client.UpdateUserAsync(_selecionado.Id, nome, email, nivel);
                    Status($"{nome} atualizado.");
                }
                await CarregarAsync();
                BtnNovo_Click(sender, e);
            }
            catch (Exception ex)
            {
                Status(ex.Message, erro: true);
            }
            finally { BtnSalvar.IsEnabled = true; }
        }

        private async void BtnRemover_Click(object sender, RoutedEventArgs e)
        {
            if (_selecionado == null)
            {
                Status("Selecione um cadastro na lista.", erro: true);
                return;
            }

            // O servidor também impede, mas avisar antes evita o clique perdido.
            if (string.Equals(_selecionado.Email, _meuEmail, StringComparison.OrdinalIgnoreCase))
            {
                Status("Você não pode remover o próprio cadastro.", erro: true);
                return;
            }

            // MessageBox qualificado: o projeto referencia WinForms e WPF, e o
            // nome existe nos dois.
            var confirma = System.Windows.MessageBox.Show(
                $"Remover \"{_selecionado.Name}\" ({_selecionado.Email})?\n\n" +
                "Ele perde o acesso à biblioteca no próximo login.\n" +
                "Sessões já abertas continuam válidas até expirarem.",
                "Remover apoiador", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirma != MessageBoxResult.Yes) return;

            BtnRemover.IsEnabled = false;
            try
            {
                await _client.DeleteUserAsync(_selecionado.Id);
                Status("Removido.");
                await CarregarAsync();
                BtnNovo_Click(sender, e);
            }
            catch (Exception ex)
            {
                Status(ex.Message, erro: true);
            }
            finally { BtnRemover.IsEnabled = true; }
        }

        private async void BtnAtualizar_Click(object sender, RoutedEventArgs e) => await CarregarAsync();
    }
}
