/*
Nom : NetUtils
Auteur : PMC 2016-2018, 2022
Description : 

# Cette librairie fournie 
  - des méthodes d'extension pour les types TcpClient (et son stream) et UdpClient 
	    - TcpClient encapsule un socket et permet de l'utiliser comme un stream ce qui confère des avantages au niveau de l'abstraction
	    - UdpClient encapsule aussi un socket
	    - Pour des nouveaux projets, je recommande de manipuler les abstractions TcpClient/NetworkStream et UdpClient directement,
	      donc sans manipuler les sockets directement.
  - des méthodes d'extension pour les socket fournies à titre patrimonial
  - des méthodes de fabrique pour des TcpClient et UdpClient
  - des validations et de la gestion d'exceptions inévitables en manipulant ces classes (fin de connexion, timeout, etc.)
      - à noter : l'espace System.Net lance parfois des exceptions pour des événements qui sont normaux/habituels, ce qui
                  aurait du normalement être plutôt géré par un autre mécanisme (paramètre de retour, etc.)
  - des méthodes simplifiées pour certains protocoles text-based (HTTP, POP/SMTP) et la gestion des codes de réponses
      

# Certaine méthodes affichent des messages d'erreurs dans stderr, que vous pouvez rediriger ailleurs
	si vous ne voulez pas que ces messages s'affichent dans la console. 
    - C'est le comportement par défaut en mode console.
    - En WinForms, vous ne les verrez pas nécessairement.
    - 


# Les méthodes d'envoi et de réception utilisent l'encodage ASCII par défaut. 
	Si vous envoyez des caractères non représentables, ils seront transformés en '?'
	J'aurais pu privilégier ANSI (Encoding.Default) mais l'encodage et le décodage sur le serveur pourrait ne pas donner le même résultat.
	J'aurais pu utiliser alors UTF8 mais il y aurait eu un cout (overhead) de commmunication réseau. Négligeable, sauf à grand échelle.
	J'aurais aussi pu lancer une exception pour vour prévenir si vous "perdez" des caractères mais ca aurait aussi été couteux en traitement.
	
# J'ai choisi de ne lancer aucune exception dans la librairie.
	C'est discutable, mais ca va dans la même idée que votre utilitaire Console développé en Prog.1.
	Je capte donc certaines exception de la librairie. 
	Certaines sont normales et habituelles, je journalise donc et vous retourne un booléen.
	Certaines sont inhabituelles et je les relancent donc. 
  Pour ces cas, la solution est probablement de corriger votre code de votre coté plutot que 
	de capter l'exception.

####	Réusinage pour ne plus exposer aucun Socket
	- serveur avec TcpListener
	- envoi/réception sur connexion TCP avec TcpClient toujours ou Stream
	- envoi/réception sur UDP avec UDPClient toujours.

##### 2022-01-12 Enlever les dépendances vers ConsoleUtils et BroadcastClient pour que le fichier soit indépendant et 
                 qu'on puisse l'ajouter comme fichier à un projet directement.

*/

//using static UtilCS.ConsoleUtils.ConsoleUtils; // retiré 2022-01-12 pour éliminer la dépendance
using System.Net;
using System.Net.Sockets;
using System.Text;
using static System.Console;

namespace Utilitaire
{
  public static class NetworkUtils
  {
    // J'ai laissé cette propriété configurable pour mes démonstrations mais comme expliqué au cours, la solution n'est jamais d'augmenter la taille de la file pour pallier à des problèmes réseaux
    public static int FileAttenteTCP = 5; // standard ... si le serveur est bien configuré il y aura jamais de client qui recevra un deny
    public static Mutex MutexSynchroConsole;

    public static string FinDeLigne = Environment.NewLine; // supporte seulement \n ou \r\n
    
    public static bool Verbose = false;
    public static ConsoleColor CouleurInfo = ConsoleColor.Cyan;
    public static string SentinelleInvite = ":"; // sert pour ignorer des caractères recus et à reconnaitre le moment où on doit envoyer
    public static string SentinelleInfo = ":"; // sert pour ignorer certains caractères et à reconnaitre le moment ou le message qu'on recherche commence
    public static string PréfixeRéception = "<<<< ";
    public static string PréfixeEnvoi = ">>>>> ";
    public static string PréfixeErreur = "*** ";
    //private static object baton = new object();

    /// <summary>
    /// Permet de créer une connexion TCP tout en fournissant tous les paramètres configurables de la librairie
    /// </summary>
    /// <param name="host"></param>
    /// <param name="portTCP"></param>
    /// <param name="finLigne"></param>
    /// <param name="verbose"></param>
    /// <param name="sentinelleInvite"></param>
    /// <param name="sentinelleInfo"></param>
    /// <returns></returns>
    internal static TcpClient InitConnexion(string host, int portTCP, string finLigne = "\r\n", bool verbose = false,
                                            string sentinelleInvite = ":", string sentinelleInfo = ":")
    {
      FinDeLigne = finLigne;
      Verbose = verbose;
      SentinelleInvite = sentinelleInvite;
      SentinelleInfo = sentinelleInfo;
      return PréparerSocketTCPConnecté(host, portTCP);
    }

    /// <summary>
    /// Méthode tout-en-un qui prépare un socket en mode écoute (la file de clients pourra commencer)
    /// et qui attend après le premier client.
    /// 
    /// Écoutera sur tous les interfaces, ce qui est généralement ce qu'on veut. Modifiez le code si ce n'est pas le cas.
    /// 
    /// La librairie utilise seulement des sockets (et non des TcpClient) pour créer des serveurs.
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    public static Socket ÉcouterTCPEtAccepter(int port)
    {
      Socket s = ÉcouterTCP(port); // crée le socket avec seulement le point de connexion local
      
      return s?.Accept(); // retourne un socket avec les 2 points de connexion ou null si le socket est null

    }

    /// <summary>
    /// Fonction bloquante qui acceptera un nouveau client. 
    /// Si vous avez vérifié en amont que le socket est "prêt", par exemple avec Select() ou autre 
    ///   mécanisme, la fonction ne bloquera pas et retournera le socket "connecté" instantanément.
    ///   
    /// Cette fonction ne fait pas grand chose de plus que le Accept() de la classe Socket 
    ///   mais je l'ai ajouté par souci de modularité, d'uniformité et d'extensibilité si 
    ///   jamais je veux ajouter des validations ou autre.
    /// </summary>
    /// <param name="socket">Un socket maitre (en mode listen seulement, pas de remote endpoint)</param>
    /// <returns>Un socket client connecté</returns>
    public static Socket Accepter(this Socket socketMaitre)
    {
      Socket socketClient;
      try
      {
        socketClient = socketMaitre.Accept();

        if (Verbose)
          AfficherInfo("Client accepté :\n\tEndpoint distant : " + socketClient.RemoteEndPoint + "\n\t" + "Endpoint local : " + socketClient.LocalEndPoint);

      }
      catch (SocketException ex)
      {
        AfficherErreur("Erreur au moment d'accepter. Peu probable mais bon." +
          "Code erreur : " + ex.ErrorCode);
        throw;
      }

      return socketClient;
    }

    private static void AfficherCommunication(string message)
    {
      if (Verbose)
      {
        if (MutexSynchroConsole != null) MutexSynchroConsole.WaitOne();

        ConsoleColor ancienneCouleur = ForegroundColor;
        ForegroundColor = ConsoleColor.Green;
        WriteLine("*** " + message);
        ForegroundColor = ancienneCouleur;
        if (MutexSynchroConsole != null) MutexSynchroConsole.ReleaseMutex();

      }
    }

    private static void AfficherEnvoi(string message) => AfficherCommunication(PréfixeEnvoi + message);



    /// <summary>
    /// Normalement on met cette fonction dans un utilitaire ailleurs, style ConsoleUtils comme en Prog.1.
    /// Il est mit ici pour rendre le fichier sans aucune dépendance et être intégré facilement à vos projets.
    /// </summary>
    /// <param name="erreur"></param>
    private static void AfficherErreur(string erreur)
    {
      // Décommentez cette ligne si vous voulez tester de façon automatique si votre synchro est bien faite !
      //if (!(Console.ForegroundColor == ConsoleColor.Yellow || Console.ForegroundColor == ConsoleColor.White))
      //  throw new Exception("console pas la bonne couleur !");

      if (MutexSynchroConsole != null) MutexSynchroConsole.WaitOne(); // Down

      ConsoleColor ancienneCouleur = ForegroundColor;
      ForegroundColor = ConsoleColor.Red;
      WriteLine(PréfixeErreur + erreur);
      ForegroundColor = ancienneCouleur;

      if (MutexSynchroConsole != null) MutexSynchroConsole.ReleaseMutex(); // Up

    }

    public static void AfficherErreur(String format, params object[] arg0) => AfficherErreur(String.Format(format, arg0));


    /// <summary>
    /// Affiche le message dans la couleur prédéfinie pour les messages informatifs
    /// </summary>
    /// <param name="info">Le message informatif</param>
    public static void AfficherInfo(string info)
    {
      // Décommentez cette ligne si vous voulez tester de façon automatique si votre synchro est bien faite ! Voir diapo #41 Présentation NetworkUtils et serveur concurrent
      //if (!(ForegroundColor == ConsoleColor.Yellow || ForegroundColor == ConsoleColor.White))
      //  throw new Exception("console pas la bonne couleur !");

      if (MutexSynchroConsole != null) MutexSynchroConsole.WaitOne(); // Down

      ConsoleColor ancienneCouleur = ForegroundColor;
      // sans mutex configuré, peut être interrompu ici et causer un problème dans un environnement multithread ou multiprocessus
      ForegroundColor = CouleurInfo;
      // ou ici
      WriteLine(info);
      // ou ici, vous aurez compris que ca peut être n'importe où
      ForegroundColor = ancienneCouleur;

      if (MutexSynchroConsole != null) MutexSynchroConsole.ReleaseMutex(); // Up

    }


    public static void AfficherInfo(String format, params object[] arg0) => AfficherInfo(String.Format(format, arg0));

    private static void AfficherRéception(string message) => AfficherCommunication(PréfixeRéception + message);

    /// <summary>
    /// TODO
    /// </summary>
    /// <param name="tcpClient"></param>
    /// <returns></returns>
    public static bool EstAccepté(this TcpClient tcpClient)
    {



      //bool accepté = taille == 0;
      return true;
    }

    /// <summary>
    /// Prépare un socket en mode écoute (la file de client pourra commencer)
    /// mais on n'attend pas après le premier client (c'est Accept() qui fait ca).
    /// 
    /// Utile pour un client itératif lorsqu'on veut Accepter explicitement un client à la fois.
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    public static Socket ÉcouterTCP(int port)
    {
      Socket s = new Socket(SocketType.Stream, ProtocolType.Tcp);
      try
      {
        s.Bind(new IPEndPoint(IPAddress.Any, port));
      }
      catch(SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) // 10048
      {
        AfficherErreur("Port déja occupé.");
        return null; // c'est au code appellant à tester le résultat
      }

      if (Verbose) AfficherInfo("Bind fait :\n\tInterface : " + s.LocalEndPoint);

      s.Listen(FileAttenteTCP); // 5 est assez standard pour la file...normalement le serveur accepte les clients assez rapidement alors ils ne restent pas dans la file
                                // À noter que c'est la limite de la file des clients non acceptés. Il pourra y en avoir plus que ca au total.

      if (Verbose)
        AfficherInfo("Écoute TCP démarée.");

      return s;
    }

    /// <summary>
    /// Méthode qui bloque jusqu'à ce qu'un des sockets recoive une communication (prêt)
    /// À noter que aucune information n'est "recue" des sockets, 
    /// elle fait seulement retourner la liste des sockets prêts.
    /// </summary>
    /// <param name="listeComplèteSockets"></param>
    /// <returns></returns>
    public static List<Socket> AttendreSocketsPrêts(this List<Socket> listeComplèteSockets)
    {
      List<Socket> listeSocketsPrêts = new List<Socket>(listeComplèteSockets);
      // C'est la méthode Select() qui fait la magie. 
      // Elle "interroge" les sockets sans bloque sur un en particulier.
      // Elle bloque donc sur l'ensemble mais on pourrait spécifier un timeout.
      Socket.Select(listeSocketsPrêts, null, null, -1); // on attend pour toujours, pas de timeout. J'assume que vous allez exécuter ce code dans un thread dédié.
      return listeSocketsPrêts;

    }
    /// <summary>
    /// Retourne le premier
    /// </summary>
    /// <param name="listeComplèteSockets"></param>
    /// <returns></returns>
    public static Socket AttendrePremierSocketPrêt(this List<Socket> listeComplèteSockets)
    {
      return AttendreSocketsPrêts(listeComplèteSockets)[0]; // retourne tjrs une liste ayant au moins un élément (will block or die trying), aucun problème à hardcoder indice 0 ici !
    }


    /// <summary>
    /// Cette méthode va créer un socket et tenter de le connecter au serveur spécifié.
    /// </summary>
    /// <param name="host"></param>
    /// <param name="portDistant"></param>
    /// <returns>null si la connexion a échoué, sinon le client lui-même</returns>
    public static TcpClient PréparerSocketTCPConnecté(string host, int portDistant)
    {
      TcpClient tcpClient = new TcpClient();

      if (tcpClient.ConnecterTCP(host, portDistant))
        return tcpClient;
      else
        return null;
    }

    /// <summary>
    /// Cette méthode va tenter de connecter un socket existant au serveur spécifié
    /// Cette méthode fait simplement l'appel à Connect mais offre un 
    /// booléen de retour en plus. BIG DEAL!!
    /// </summary>
    /// <param name="tcpClient">le socket à connecter (extension de TcpClient)</param>
    /// <param name="host"></param>
    /// <param name="portDistant"></param>
    /// <returns>faux si la connexion a échoué</returns>
    public static bool ConnecterTCP(this TcpClient tcpClient, IPEndPoint remoteEndPoint)
    {
      return tcpClient.ConnecterTCP(remoteEndPoint.Address.ToString(), remoteEndPoint.Port);
    }
    public static bool ConnecterTCP(this TcpClient tcpClient, string host, int portDistant)
    {
      if (tcpClient == null) throw new InvalidOperationException("Veuillez instancier votre TcpClient."); // en dehors du try pour éviter qu'il soit attrapé par le catch fallback

      try
      {

        tcpClient.Connect(host, portDistant);

        if (Verbose) AfficherInfo("Connexion réussie.");

        return true;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) // 10061
      {
        // Refus du serveur, normalement cela arrive juste sur la même machine.
        AfficherErreur("Erreur de connexion. Refus du serveur local.");
        return false;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) // 10060
      {
        // Timeout, vérifiez le IP
        AfficherErreur("Erreur de connexion. Vérifiez le IP.");
        return false;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound) // 11001
      {
        // Erreur 11001 host not found
        AfficherErreur("Erreur de connexion. Vérifiez votre DNS.");
        return false;
      }
      catch (SocketException ex)
      {
        // problème réseau, journaliser et relancer selon vos besoins
        AfficherErreur("Erreur de connexion. Problème réseau inconnu.");
        throw;
      }
      catch (Exception ex)
      {
        // problème de plus bas niveau, journaliser et relancer
        AfficherErreur("Erreur de connexion. Problème de bas niveau.");
        throw;
      }


    }


    /// <summary>
    /// UDP n'est jamais connecté. "Connecter" un socket UDP permet seulement de ne pas avoir à spécifier le destinataire
    /// à chaque envoi, ce qui n'est pas le cas ici.
    /// </summary>
    /// <returns>un socket prêt à être utiliser pour envoyer à n'importe qui, en spécifiant le remoteEndPoint chaque fois </returns>
    public static UdpClient PréparerSocketUDPNonConnectéPourEnvoiMultiple()
    {
      UdpClient udpClient = new UdpClient();
      return udpClient;
    }

    /// <summary>
    /// Sert dans des cas spécifiques où l'on veut un socket non connecté 
    /// et associé à un interface en particulier.
    /// </summary>
    /// <param name="ipInterface"></param>
    /// <returns></returns>
    public static UdpClient PréparerSocketUDPNonConnectéSurInterface(IPAddress ipInterface)
    {
      // Le port 0 signifie "prendre n'importe quel port local disponible"
      // on n'a pas besoin de le connaitre puisque ce socket agira comme client
      UdpClient udpClient = new UdpClient(new IPEndPoint(ipInterface, 0));
      return udpClient;
    }

    /// <summary>
    /// En étant connecté, on recoit et envoie toujours au même
    /// point de connexion
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="portDistant"></param>
    /// <param name="localEndPoint">on utilise ce paramètre seulement pour des cas spécifiques, comme quand on veut absolument que le traffic sorte par un interface spécifique, laissez le null dans le doute</param>
    /// <returns></returns>
    public static UdpClient PréparerSocketUDPConnecté(string hostname, int portDistant, IPAddress ipInterfaceLocal = null)
    {
      UdpClient udpClient;
      if (ipInterfaceLocal == null)
        udpClient = new UdpClient();
      else
        udpClient = new UdpClient(new IPEndPoint(ipInterfaceLocal, 0)); // n'importe quel port

      udpClient.ConnecterUDP(hostname, portDistant);
      return udpClient;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="portDistant"></param>
    /// <returns></returns>
    public static UdpClient PréparerSocketUDPConnectéPourDiffusion(int portDistant)
    {
      UdpClient udpClient = new UdpClient();

      udpClient.ConnecterUDPPourDiffusion(portDistant);
      return udpClient;
    }



    /// <summary>
    /// Ne "connecte" pas vraiment comme avec TCP. 
    /// Le Connect de UDP permet simplement d'éviter d'avoir à spécifier 
    /// la destination à chaque Send ultérieur.
    /// </summary>
    /// <param name="udpClient">le client</param>
    /// <param name="portDistant">le port "remote", cette méthode ne permet pas de choisir le port sortant</param>
    /// <returns></returns>
    public static bool ConnecterUDPPourDiffusion(this UdpClient udpClient, int portDistant)
    {
      try
      {
        udpClient.Connect(new IPEndPoint(IPAddress.Broadcast, portDistant));

        if (Verbose)
          WriteLine("L'adresse de diffusion {0} sera utilisée.", IPAddress.Broadcast);

        return true;
      }
      catch (SocketException)
      {
        AfficherErreur("Impossible de préparer un socket pour diffusion vers le port {0}.", portDistant);
        return false;
      }
    }

    /// <summary>
    /// Ne "connecte" pas vraiment comme avec TCP. 
    /// Le Connect de UDP permet simplement d'éviter d'avoir à spécifier 
    /// la destination à chaque Send ultérieur.
    /// </summary>
    /// <param name="udpClient"></param>
    /// <param name="hostname"></param>
    /// <param name="portDistant">le port "remote", cette méthode ne permet pas de choisir le port sortant</param>
    /// <returns></returns>
    public static bool ConnecterUDP(this UdpClient udpClient, string hostname, int portDistant)
    {
      try
      {

        udpClient.Connect(hostname, portDistant);

        if (Verbose)
          WriteLine("Socket UDP prêt. Pas vraiment connecté, mais prêt à recevoir ou envoyer.");

        return true;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
      {
        AfficherErreur("Erreur de connexion. Vérifiez votre DNS.");
      }
      catch (SocketException ex)
      {
        AfficherErreur("Erreur de connexion code : " + ex.ErrorCode);

      }
      return false;
    }

    /// <summary>
    /// Tente de démarrer l'écoute sur le premier port UDP
    /// disponible à partir du port de début fourni.
    /// </summary>
    /// <param name="portDépart">Port de départ, sera ajusté au port actuel</param>
    /// <param name="clientUDP">L'objet représentant la connexion</param>
    /// <returns>Le port actuel d'écoute ou -1 si impossible de démarrer l'écoute</returns>
    public static bool ÉcouterUDPPremierPortDisponible(int portDépart, out UdpClient clientUDP)
    {
      for (int i = 0; i < 10; ++i)
      {
        try
        {
          clientUDP = new UdpClient();
          IPEndPoint localEndpoint = new IPEndPoint(IPAddress.Any, portDépart + i);
          clientUDP.Client.Bind(localEndpoint);

          return true;
        }
        catch (SocketException)
        {
          AfficherErreur("Erreur de création du socket");
          clientUDP = null;

        }
      }
      clientUDP = null;
      return false;
    }

    /// <summary>
    /// Créé un socket UDP et l'initialise pour recevoir
    /// sur l'interface spécifié
    /// </summary>
    /// <param name="port"></param>
    /// <param name="ipInterfaceÉcoute">ou fournir Any pour écouter sur tous les interfaces</param>
    /// <returns></returns>
    public static UdpClient PréparerSocketUDPPourRéception(int port, IPAddress ipInterfaceÉcoute)
    {
      UdpClient udpClient;
      if (!ÉcouterUDP(port, out udpClient, ipInterfaceÉcoute))
        AfficherErreur("Impossible de préparer l'écoute.");

      return udpClient; // peu importe si c'est null

    }


    /// <summary>
    /// Créé un socket UDP et l'initialise pour recevoir
    /// sur toutes les interfaces
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    public static UdpClient PréparerSocketUDPPourRéception(int port)
    {
      return PréparerSocketUDPPourRéception(port, IPAddress.Any);


    }

    /// <summary>
    /// Initialise un socket existant UDP pour recevoir
    /// sur toutes les interfaces
    /// </summary>
    /// <returns>vrai si le bind réussi</returns>
    public static bool ÉcouterUDP(int port, out UdpClient clientUDP)
    {
      return ÉcouterUDP(port, out clientUDP, IPAddress.Any);
    }



    /// <summary>
    /// Initialise un socket existant UDP pour recevoir
    /// sur l'interface spécifié
    /// </summary>
    /// <returns>vrai si le bind réussi</returns>
    public static bool ÉcouterUDP(int port, out UdpClient clientUDP, IPAddress ipInterfaceÉcoute)
    {
      try
      {
        clientUDP = new UdpClient();
        IPEndPoint localEndpoint = new IPEndPoint(ipInterfaceÉcoute, port);
        clientUDP.Client.Bind(localEndpoint);

        if (Verbose)
          AfficherInfo("Bind effectué sur : " + localEndpoint.ToString());

        return true;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
      {
        AfficherErreur("Erreur de création du socket. Port UDP déja en utilisation.");
        clientUDP = null;

      }
      return false;
    }

    /// <summary>
    /// ATTENTION, NE MARCHE PAS PARFAITEMENT, MÊME SI PLUSIEURS SOCKET ÉCOUTENT SUR LE MEME PORT UDP, UN SEUL RECOIT LE MESSAGE
    /// CA PEUT ÊTRE ACCEPTABLE DANS CERTAINS CAS, MAIS C'EST RAREMENT CE QU'ON VEUT.
    /// </summary>
    /// <param name="port"></param>
    /// <param name="clientUDP"></param>
    /// <returns></returns>
    public static bool ÉcouterUDPRéutilisable(int port, out UdpClient clientUDP)
    {
      try
      {
        clientUDP = new UdpClient();
        // La réutilisation du socket permettra de partir plusieurs clients NP qui écouteront tous sur le port UDP
        clientUDP.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        clientUDP.EnableBroadcast = true;
        IPEndPoint localEndpoint = new IPEndPoint(IPAddress.Any, port);

        clientUDP.Client.Bind(localEndpoint);

        return true;
      }
      catch (SocketException)
      {
        WriteLine("Erreur de création du socket");
        clientUDP = null;

      }
      return false;
    }

    public static bool EnvoyerEtRecevoirLigne(this TcpClient c, string messageÀEnvoyer, out string réponse)
    {
      c.EnvoyerMessage(messageÀEnvoyer);
      return c.RecevoirLigne(out réponse);

    }

    public static bool EnvoyerEtRecevoirLigneAprèsSentinelle(this TcpClient c, string messageÀEnvoyer, string sentinelle, out string réponse)
    {
      c.EnvoyerMessage(messageÀEnvoyer);
      return c.RecevoirLigneAprèsSentinelle(sentinelle, out réponse);

    }

    public static bool EnvoyerEtRecevoirLigneAprèsSentinelleInvite(this TcpClient c, string messageÀEnvoyer, out string réponse)
    {

      return c.EnvoyerEtRecevoirLigneAprèsSentinelle(messageÀEnvoyer, SentinelleInvite, out réponse);

    }

    public static bool EnvoyerEtRecevoirLigneAprèsSentinelleInfo(this TcpClient c, string messageÀEnvoyer, out string réponse)
    {
      c.EnvoyerMessage(messageÀEnvoyer);
      return c.RecevoirLigneAprèsSentinelleInfo(out réponse);

    }

    public static bool RecevoirLigneAprèsSentinelle(this TcpClient c, string sentinelle, out string réponse)
    {
      c.IgnorerJusqua(sentinelle);
      return c.RecevoirLigne(out réponse);
    }

    public static bool RecevoirLigneAprèsSentinelleInfo(this TcpClient c, out string réponse)
    {
      c.IgnorerJusqua(SentinelleInfo);
      return c.RecevoirLigne(out réponse);
    }

    /// <summary>
    /// Utilise RecevoirMessage donc lié au buffering TCP
    /// Privilégier EnvoyerEtRecevoirLigne ou un truc du genre ..
    /// </summary>
    /// <param name="c"></param>
    /// <param name="message"></param>
    /// <param name="réponse"></param>
    /// <returns></returns>
    public static bool EnvoyerEtObtenirRéponse(this TcpClient c, string message, out string réponse)
    {
      c.EnvoyerMessage(message);
      return c.RecevoirMessage(out réponse);

    }

    /// <summary>
    /// Envoie la requête (et ajoute la fin de ligne) et 
    /// recoit une ligne représentant la réponse.
    /// Pour les protocoles avec des réponses multilignes, considérez utiliser RecevoirJusquaCode()
    /// </summary>
    /// <param name="s"></param>
    /// <param name="requête"></param>
    /// <param name="réponse"></param>
    /// <returns></returns>
    public static bool EnvoyerRequeteEtObtenirRéponse(this Stream s, string requête, out string réponse)
    {
      s.EnvoyerMessage(requête);
      return s.RecevoirLigne(out réponse);
    }



    /// <summary>
    /// Considérez utiliser TcpClient ou Stream
    /// Cette surcharge permet d'envoyer un array de byte ** complètement ** , sans ajout de ligne et sans paramètre d'encodage.
    /// Utile seulement si on veut envoyez des données binaires possiblement non représentables avec les encodages disponibles ASCII, ANSI (Default) ou UTF8.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="données"></param>
    public static bool EnvoyerMessage(this Socket s, byte[] données)
    {
      return EnvoyerMessage(s, données, données.Length);
    }

    /// <summary>
    /// Considérez utiliser TcpClient ou Stream
    /// Cette surcharge permet d'envoyer un array de byte complètement, sans ajout de ligne et sans paramètre d'encodage.
    /// Permet d'envoyer ** partiellement ** un array de byte.
    /// Utile seulement si on veut envoyez des données binaires possiblement non représentables avec les encodages disponibles ASCII, ANSI (Default) ou UTF8.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="données">le buffer au complet</param>
    /// <param name="taille">le nb d'octets du buffer à envoyer </param>
    public static bool EnvoyerMessage(this Socket s, byte[] données, int taille)
    {
      try
      {
        taille = s.Send(données, taille, SocketFlags.None);
        return true;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
      {
        AfficherErreur("Déconnexion du endpoint distant pendant l'envoi. Plutot rare, cela signifie probablement que le thread bloquait sur le send, ce qui veut dire que votre solution est mal conçue.");
        return false;
      }
      catch (SocketException ex)
      {
        AfficherErreur("Erreur de socket au moment de l'envoi. Code : " + ex.ErrorCode + "(" + ex.SocketErrorCode + ")");
        throw;
      }
    }

    /// <summary>
    /// Permet d'envoyer une string dans l'encodage par défaut ou selon l'encodage fourni.
    /// Ajoutera une fin de ligne par défaut, ce qui n'est pas nécessairement ce que vous voulez.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <param name="ajouterFinLigne"></param>
    /// <param name="encodage"></param>
    /// <returns></returns>
    public static bool EnvoyerMessage(this Socket s, string message, bool ajouterFinLigne = true, string encodage = "ASCII")
    {
      var encoding = Encoding.GetEncoding(encodage);
      return EnvoyerMessage(s, message, ajouterFinLigne, encoding);
    }

    public static void EnvoyerMessageAvecTimeout(this Socket s, string message, int délaiTimeout, out bool finConnexion, out bool timeout, bool ajouterFinLigne = true, string encodage = "ASCII")
    {
      var encoding = Encoding.GetEncoding(encodage);
      timeout = false;
      finConnexion = false;
      int vieuxDélaiTimeout = s.SendTimeout;
      s.SendTimeout = délaiTimeout;
      try
      {
        bool envoiRéussi = EnvoyerMessage(s, message, ajouterFinLigne, encoding);
        timeout = !envoiRéussi;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
      {
        if (s.Connected)
          timeout = true;
        else
          finConnexion = true;
      }
      catch (InvalidOperationException)
      {
        AfficherEnvoi("Vous tentez d'envoyer sur une connexion fermée.");
        finConnexion = true;
      }

      s.SendTimeout = vieuxDélaiTimeout;

    }

    public static bool EnvoyerMessage(this Socket s, string message, bool ajouterFinLigne, Encoding encoding)
    {
      if (ajouterFinLigne)
        message += FinDeLigne;

      AfficherEnvoi(message);

      byte[] données = encoding.GetBytes(message);

      return EnvoyerMessage(s, données);

    }

    /// <summary>
    /// Redirige l'appel à tcpclient.GetStream()
    /// Non fonctionnera pas si vous êtes sur une connexion sécurisée.
    /// Dans ce cas, utilisez plutot votre propre stream Ssl.
    /// </summary>
    /// <param name=""></param>
    /// <param name="message"></param>
    /// <param name="ajouterFinLigne">fin de ligne Windows</param>
    public static bool EnvoyerMessage(this TcpClient client, string message, bool ajouterFinLigne = true, string encodage = "ASCII")
    {
      //throw new InvalidOperationException("Utilisez la méthode sur votre stream directement");
      return client.GetStream().EnvoyerMessage(message, ajouterFinLigne, encodage);
    }

    public static bool EnvoyerMessage(this TcpClient client, char c, bool ajouterFinLigne = true, string encodage = "ASCII")
    {
      string s = c + "";
      //throw new InvalidOperationException("Utilisez la méthode sur votre stream directement");
      return client.GetStream().EnvoyerMessage(s, ajouterFinLigne, encodage);
    }


    /// <summary>
    /// Pour TCP (ou SSL), on ajoute une fin de ligne puisque TCP est stream-orienté, rien ne nous
    /// garanti que le message sera reçu en un morceau sur le fil même si
    /// envoyé en un seul appel à Send à partir d'ici.
    /// </summary>
    /// <param name="s">le buffer interne du socket TCP</param>
    /// <param name="message"></param>
    /// <param name="ajouterFinLigne">mettre à false si votre message contient déja un \n</param> 
    //public static void EnvoyerMessage(this Stream s, string message, bool ajouterFinLigne = true, string encodage = "ASCII")
    //{
    //	EnvoyerMessage((s, message, ajouterFinLigne, encodage);
    //}

    /// <summary>
    /// 
    /// </summary>
    /// <param name="s"></param>
    /// <param name="messageÀRecevoir"></param>
    /// <param name="messageÀEnvoyer"></param>
    /// <param name="ajouterFinLigne"></param>
    public static bool EnvoyerMessageAprès(this Stream s, string messageÀRecevoir, string messageÀEnvoyer, bool ajouterFinLigne = true)
    {
      // recevoir jusqu'à
      if (!s.RecevoirMessagesJusqua(out string messageRecu, messageÀRecevoir))
      {
        AfficherErreur("Erreur de réception.");
        return false;
      }

      if (!s.EnvoyerMessage(messageÀEnvoyer, ajouterFinLigne))
      {
        AfficherErreur("Erreur d'envoi après réception.");
        return false;
      }

      return true;
    }


    /// <summary>
    /// TCP
    /// </summary>
    /// <returns>vrai si l'envoi est un succès; vous n'êtes pas obligé nécessairement de capter la valeur de retour</returns>
    public static bool EnvoyerMessage(this Stream s, string message, bool ajouterFinLigne = true, string encodage = "ASCII")
    {
      if (ajouterFinLigne)
        message += FinDeLigne;

      byte[] données = Encoding.GetEncoding(encodage).GetBytes(message);
      try
      {
        s.Write(données, 0, données.Length);
        AfficherEnvoi(message);
      }
      catch (IOException ex) when (ex.InnerException is SocketException)
      {
        SocketException socketEx = ex.InnerException as SocketException;
        if (socketEx.SocketErrorCode != SocketError.ConnectionReset) throw;// 10054

        AfficherErreur("Impossible d'envoyer sur ce socket car l'autre point de connexion a été déconnecté");
        return false;

      }
      catch(Exception ex)
      {
        AfficherErreur("Erreur lors de l'envoi sur le stream tcp. Détails exception : " + ex);
        throw;
      }

      return true;
    }

    /// <summary>
    /// UDP
    /// Utile lorsque le destinaire a déjà été spécifié dans le Connect
    /// </summary>
    /// <param name="s">un client UDP déja connecté</param>
    /// <param name="message"></param>
    public static void EnvoyerMessage(this UdpClient s, string message, string encodage = "ASCII")
    {
      s.EnvoyerMessage(message, Encoding.GetEncoding(encodage));
    }

    public static void EnvoyerMessage(this UdpClient s, string message, Encoding encoding)
    {
      byte[] données = encoding.GetBytes(message);
      s.Send(données, données.Length);
      AfficherEnvoi(message);
    }

    /// <summary>
    /// UDP Équivalent du SendTo
    /// </summary>
    /// <param name="s">le client UDP non connecté</param>
    /// <param name="message"></param>
    /// <param name="destinataire">le destinataire</param>
    public static void EnvoyerMessage(this UdpClient s, string message, IPEndPoint destinataire, string encodingString = "ASCII")
    {
      Encoding encoding = Encoding.GetEncoding(encodingString);
      EnvoyerMessage(s, message, destinataire, encoding);
    }

    /// <summary>
    /// UDP
    /// </summary>
    public static void EnvoyerMessage(this UdpClient s, string message, IPEndPoint destinataire, Encoding encoding)
    {
      byte[] données = encoding.GetBytes(message);
      s.Send(données, données.Length, destinataire);
      AfficherEnvoi(message);
    }

    /// <summary>
    /// Sert à recevoir une ligne et ignorer la valeur de retour.
    /// C'est seulement un bonbon syntaxique pour éviter d'utiliser Recevoir si on a pas besoin de la valeur retournée.
    /// </summary>
    /// <param name="tcpClient"></param>
    /// <returns></returns>
    public static bool IgnorerLigne(this TcpClient tcpClient)
    {
      return tcpClient.GetStream().RecevoirLigne(out _);
    }

    public static bool IgnorerLignes(this TcpClient tcpClient, int nbLignes)
    {
      if (nbLignes < 1) throw new ArgumentException("Veuillez spécifier le nombre de lignes");

      for (int i = 0; i < nbLignes; i++)
      {
        tcpClient.IgnorerLigne();
      }

      return true;

    }

    /// <summary>
    /// Sert à tout recevoir et tout ignorer jusqu'à une certaine sentinelle configurable (propriété publique)
    /// C'est seulement un bonbon syntaxique pour éviter d'utiliser RecevoirJusquaInvite si on a pas besoin de la valeur retournée.
    /// </summary>
    public static bool IgnorerJusquaInvite(this TcpClient tcp)
    {
      return tcp.GetStream().RecevoirJusqua(out _, SentinelleInvite);
    }

    /// <summary>
    /// Sert à tout recevoir et tout ignorer jusqu'à une certaine sentinelle fournie en paramètre
    /// C'est seulement un bonbon syntaxique pour éviter d'utiliser RecevoirJusquaSentinelle si on a pas besoin de la valeur retournée.
    /// </summary>
    public static bool IgnorerJusqua(this TcpClient tcp, string sentinelle)
    {
      return tcp.GetStream().RecevoirJusqua(out _, sentinelle);
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <returns>vrai si reçu quelque chose, faux lors de déconnexion normale ou anormale, ou pour erreur générale</returns>
    public static bool RecevoirMessage(this Socket s, out string message)
    {
      return s.RecevoirMessage(out message, out _); // C# 7 discard
    }

    /// <summary>
    /// Prenez cette surcharge pour avoir plus de détails sur la raison de la déconnexion.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <param name="déconnexionNormale"></param>
    /// <returns></returns>
    public static bool RecevoirMessage(this Socket s, out string message, out bool déconnexionNormale)
    {
      try
      {
        byte[] données = new byte[256];
        int taille = s.Receive(données);
        message = Encoding.ASCII.GetString(données, 0, taille);
        déconnexionNormale = taille == 0; // si on a reçu quelque chose, il n'y a pas de déconnexion tout court
        return taille > 0; // reçu quelque chose 
      }
      catch (SocketException ex)
      {
        // Il est normal d'avoir une erreur 10054 connection reset quand l'autre détruit la connexion sans appeler un close en bonne et dû forme
        if (ex.ErrorCode != 10054) // For more information about socket error codes, see the Windows Sockets version 2 API error code documentation in MSDN.
          AfficherErreur("Erreur anormale de réception code : " + ex.ErrorCode);
        message = "";
        déconnexionNormale = false;
        return false;
      }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message">sera affiché après la fermeture</param>
    /// <returns></returns>
    public static bool FermerAvecAffichage(this Stream s, string message)
    {
      // Avec le stream il y a une seule étape et non deux comme avec le socket (shutdown, close)
      s.Close();
      WriteLine(message);
      return true;
    }

    /// <summary>
    /// Affichage dans stdout
    /// </summary>
    /// <param name="tcpClient"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    //public static bool FermerAvecAffichage(this TcpClient tcpClient, string message)
    //{
    //  return tcpClient.FermerAvecAffichage(message);
    //}

    /// <summary>
    /// 
    /// </summary>
    /// <param name="tcpClient"></param>
    public static void Fermer(this TcpClient tcpClient)
    {
      if (Verbose)
        WriteLine("Fermeture du stream TCP ...");

      tcpClient.GetStream().Close(); // équivalent seulement au Shutdown du socket ?

      if (Verbose)
        WriteLine("Fermeture du client TCP ...");

      tcpClient.Close();

      if (Verbose)
        WriteLine("Fermeture terminée.");
    }

    public static void Fermer(this Socket s)
    {
      if (Verbose)
        WriteLine("Désactivation des communication sur le socket ...");

      s.Shutdown(SocketShutdown.Both);

      if (Verbose)
        WriteLine("Communications terminées. Fermeture et libération des ressources en cours...");

      s.Close();

      if (Verbose)
        WriteLine("Fermeture terminée.");
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="tcp"></param>
    /// <param name="message"></param>
    /// <param name="messageARecevoir">la sentinelle</param>
    /// <returns></returns>
    public static bool RecevoirJusqua(this TcpClient tcp, out string message, string messageARecevoir)
    {
      return tcp.GetStream().RecevoirJusqua(out message, messageARecevoir);
    }

    /// <summary>
    /// Recoit caractère par caractère au lieu de par ligne ou pas message TCP
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <param name="messageARecevoir"></param>
    /// <returns></returns>
    public static bool RecevoirJusqua(this Stream s, out string message, string messageARecevoir)
    {
      try
      {

        message = "";

        int c;
        for (; ; )
        {
          c = s.ReadByte();
          /***/
          if (c == -1) break;
          /***/

          message += (char)c;

          if (message.EndsWith(messageARecevoir)) break;
        }

        AfficherRéception(message);

        return message.Length > 0; // corrigé 2016-05-19 au lieu de return true
      }
      catch (Exception ex)
      {
        AfficherErreur(ex.ToString());
      }
      message = null;
      return false;
    }

    /// <summary>
    /// TODO pour remplacer RecevoirJusquaPoint et RecevoirLigne
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <param name="code">On cherche une ligne entière spécifique. Pourrait être "\r\n.\r\n" ou simplement "\n"</param>
    /// <returns></returns>
    public static bool RecevoirLignesJusquaLigneCodée(this Stream s, out string message, string ligneCodée)
    {
      message = "";
      string ligne;
      while (s.RecevoirLigne(out ligne) && ligne != ligneCodée)
      {
        message += ligne;
      }
      return true;
    }

    public static bool ConsommerLignesJusquaLigneSeTerminantPar(this Stream s, string suffixeLigne)
    {
      RecevoirLignesJusquaLigneSeTerminantPar(s, out string message, suffixeLigne);
      return true;
    }

    /// <summary>
    /// Redirige l'appel à tcpclient.GetStream()
    /// Non fonctionnera pas si vous êtes sur une connexion sécurisée.
    /// Dans ce cas, utilisez plutot votre propre stream Ssl.
    /// </summary>
    /// <param name="tcpClient"></param>
    /// <param name="message"></param>
    /// <returns>vrai si quelque chose a été reçu</returns>
    public static bool RecevoirLigne(this TcpClient tcpClient, out string message, bool afficher = true)
    {
      return tcpClient.GetStream().RecevoirLigne(out message);
    }

    /// <summary>
    /// Bonbon syntaxique pour certaines situations et alléger le code.
    /// Va recevoir une ligne entière après avoir ignoré tout jusqu'à la sentinelle d'invite préconfigurée.
    /// </summary>
    public static bool RecevoirLigneAprèsInvite(this TcpClient s, out string message, bool afficher = true)
    {
      s.IgnorerJusquaInvite();
      return s.RecevoirLigne(out message, afficher);

    }

    public static bool RecevoirLigneAprèsSentinelleInfo(this TcpClient s, out string message, bool afficher = true)
    {
      s.IgnorerJusqua(SentinelleInfo);
      return s.RecevoirLigne(out message, afficher);

    }

    /// <summary>
    /// Pour TCP seulement
    /// Va attendre de recevoir la séquence \r\n et bloquer (attendre) au besoin.
    /// </summary>
    /// <returns >Vrai si quelque chose a été reçu, faux si déconnecté ou si rien recu</returns>
    /// <remarks>Vous pourriez recevoir vrai et être quand même déconnecté. Il y a la propriété Connected du socket qui sera mise à jour</remarks>
    public static bool RecevoirLigne(this Stream s, out string message)
    {
      try
      {
        bool finDeLigneWindows = FinDeLigne == Environment.NewLine;

        List<Byte> données = new List<byte>();
        int c;
        for (; ; )
        {
          c = s.ReadByte();
          if (c == -1) break;
          if (finDeLigneWindows && c == (int)'\r' && s.ReadByte() == (int)'\n') break;
          else if (!finDeLigneWindows && c == '\n') break;

          données.Add((byte)c);
        }

        message = Encoding.ASCII.GetString(données.ToArray(), 0, données.Count);

        AfficherRéception(message);


        return données.Count > 0; // corrigé 2016-05-19 au lieu de return true
      }
      catch (Exception ex)
      {
        AfficherErreur(ex.ToString());

      }
      message = null;
      return false;
    }

    public static bool RecevoirLignesJusquaLigneSeTerminantPar(this Stream s, out string message, string suffixeLigne)
    {
      message = "";
      string ligne;
      while (s.RecevoirLigne(out ligne) && !ligne.EndsWith(suffixeLigne)) ;

      message = ligne;

      return true;
    }

    /// <summary>
    /// Utilisez seulement cette méthode pour recevoir des données non textuelles
    /// ou pour une chaine que vous ne connaissez pas nécessairement l'encodage.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="données">un buffer déja alloué</param>
    /// <param name="taille">le nb d'octets écrits dans le buffer</param>
    /// <returns></returns>
    public static bool RecevoirMessage(this Socket s, ref byte[] données, out int taille)
    {
      try
      {
        taille = s.Receive(données);
        return taille > 0; // corrigé 2018-03-06 au lieu de return true toujours
      }
      catch (SocketException ex)
      {
        // Il est normal d'avoir une erreur 10054 connection reset quand l'autre détruit la connexion sans appeler un close en bonne et dû forme
        if (ex.ErrorCode != 10054) // For more information about socket error codes, see the Windows Sockets version 2 API error code documentation in MSDN.
          AfficherErreur("Erreur anormale de réception code : " + ex.ErrorCode);
        données = null;
        taille = 0;
        return false;
      }
    }

    /// <summary>
    /// Recoit des messages TCP jusqu'à ce que le message complet se termine par la valeur spécifiée.
    /// Privilégier RecevoirJusqua si vous n'êtes pas certain de comment les messages TCP sont envoyés.
    /// Privilégier RecevoirLignesJusqua si vous savez que ce que vous cherchez sera une fin de ligne.
    /// </summary>
    public static bool RecevoirMessagesJusqua(this TcpClient tcpClient, out string message, string messageÀRecevoir)
    {
      return tcpClient.GetStream().RecevoirMessagesJusqua(out message, messageÀRecevoir);
    }
    /// <summary>
    /// Recoit des messages TCP jusqu'à ce que le message complet se termine par la valeur spécifiée.
    /// Privilégier RecevoirJusqua si vous n'êtes pas certain de comment les messages TCP sont envoyés.
    /// Privilégier RecevoirLignesJusqua si vous savez que ce que vous cherchez sera une fin de ligne.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <param name="messageÀRecevoir"></param>
    /// <returns></returns>
    public static bool RecevoirMessagesJusqua(this Stream s, out string message, string messageÀRecevoir)
    {
      message = "";
      string portionDuMessage;
      while (true)
      {
        if (!s.RecevoirMessage(out portionDuMessage)) return false;

        message += portionDuMessage;
        /***/
        if (portionDuMessage.EndsWith(messageÀRecevoir)) break;
        /***/

      }
      return true;
    }



    /// <summary>
    /// Recoit tout (ligne ou non) jusqu'au point (utile pour le protocole POP3 et autre...)
    /// Le point est exclu.
    /// 
    /// Bug connu : pourrait ne pas détecter le .\r\n si d'autre contenu a été 
    /// envoyé d'avance par le serveur. Par exemple si on envoi LIST et une autre commande
    /// avant de faire le premier Recevoir(). 
    /// 
    /// De toute facon, vous devriez normalement récupérer les réponses
    /// du serveur au fur et à mesure et ce problème n'arrivera pas.
    /// 
    /// Pour corriger complètement, il faudrait y aller caractère par
    /// caractère comme la fonction RecevoirLigne()
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <returns>vrai si succès</returns>
    public static bool RecevoirMessagesJusquaPoint(this Stream s, out string message)
    {
      message = "";
      string portionDuMessage;
      while (true)
      {
        if (!s.RecevoirMessage(out portionDuMessage)) return false;

        message += portionDuMessage;
        if (portionDuMessage.EndsWith(".\r\n"))
        {
          // TODO : enlever le .\r\n
          break;
        }

      }
      return true;
    }




    /// <summary>
    /// Redirige l'appel à tcpclient.GetStream()
    /// Non fonctionnera pas si vous êtes sur une connexion sécurisée.
    /// Dans ce cas, utilisez plutot votre propre stream Ssl.
    /// </summary>
    /// <param name="tcpClient"></param>
    /// <param name="message"></param>
    /// <returns>vrai si quelque chose a été reçu (succès)</returns>
    public static bool RecevoirMessage(this TcpClient tcpClient, out string message)
    {
      bool res = tcpClient.GetStream().RecevoirMessage(out message);

      //if (Verbose)
      AfficherRéception(message);

      return res;
    }


    public static void RecevoirMessageAvecTimeout(this TcpClient tcpClient, out string message, int délaiTimeout, out bool finConnexion, out bool timeout)
    {
      timeout = false;
      finConnexion = false;
      message = string.Empty;

      int vieuxDélaiTimeout = tcpClient.ReceiveTimeout; // normalement 0 (blocking)
      tcpClient.ReceiveTimeout = délaiTimeout;
      try
      {
        bool reçuQuelqueChose = tcpClient.GetStream().RecevoirMessage(out message);
        timeout = !reçuQuelqueChose;

      }
      catch (IOException ex) when (ex.InnerException is SocketException && (ex.InnerException as SocketException).SocketErrorCode == SocketError.TimedOut)
      {
        // le problème est que ca donne le même code pour un vrai timeout et une déconnexion
        if (tcpClient.Connected)
          timeout = true;
        else
          finConnexion = true;
      }
      catch (InvalidOperationException)
      {
        AfficherErreur("Vous tentez de recevoir sur une connexion fermée.");
        finConnexion = true;
      }
      // On remet le timeout correctement, probablement à 0 (blocking)
      tcpClient.ReceiveTimeout = vieuxDélaiTimeout;

    }


    /// <summary>
    /// J'ai généralisé cette fonction de NetworkStream à Stream pour supporter SslStream
    /// Si vous implémentez un protocole texte basé sur des lignes entières,
    /// considérez utiliser RecevoirLigne() ou RecevoirJusquaCode()
    /// Le message sera de taille maximale de 256 octets ce qui n'est
    /// pas un problème en soi puisqu'on peut appeller cette méthode en boucle.
    /// Considérez utiliser RecevoirTout() si vous voulez recevoir tout jusqu'à la fin de la connexion.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <returns>vrai si quelque chose a été reçu (succès), faux indique une fin de connexion</returns>
    public static bool RecevoirMessage(this Stream s, out string message, string encodingString = "ASCII")
    {
      var encoding = Encoding.GetEncoding(encodingString);
      return RecevoirMessage(s, out message, encoding);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="s"></param>
    /// <param name="message"></param>
    /// <param name="encoding"></param>
    /// <returns>vrai si quelque chose a été reçu (succès), faux indique une fin de connexion</returns>
    public static bool RecevoirMessage(this Stream s, out string message, Encoding encoding) // on ne peut utiliser Encoding.ASCII comme valeur par défaut, ce n'est pas une constante mais plutot une variable readonly
    {
      try
      {
        byte[] données = new byte[256];
        int taille = s.Read(données, 0, données.Length);
        message = encoding.GetString(données, 0, taille);

        AfficherRéception(message); // Pas besoin de tester la verbosité ici

        return taille > 0; // au lieu de return true toujours, une réception de taille 0 signifie toujours la fin de connexion (normale)
      }
      catch (IOException ex) when (ex.InnerException is SocketException && (ex.InnerException as SocketException).SocketErrorCode == SocketError.TimedOut)
      {
        if (Verbose)
          WriteLine("Timeout lors de la réception.");

        throw; // on va la capter plus haut
      }
      catch (IOException ex)
      {
        // https://msdn.microsoft.com/query/dev14.query?appId=Dev14IDEF1&l=EN-US&k=k%28System.Net.Sockets.NetworkStream.Read%29;k%28DevLang-csharp%29&rd=true
        AfficherErreur("Erreur de réception, la connexion a été fermée." + ex.Message);
      }
      catch (ObjectDisposedException ex)
      {
        AfficherErreur("Erreur de réception, le stream est fermé ou erreur de lecture sur le réseau." + ex.Message);
      }


      message = null;
      return false;
    }


    public static bool RecevoirTout(this TcpClient s, out string messageComplet)
    {
      return RecevoirTout(s.GetStream(), out messageComplet);
    }

    /// <summary>
    /// Si vous appellez cette méthode, une réception sera faite
    /// jusqu'à temps que la connexion soit fermée par l'autre point de connexion et ce,
    /// peu importe si c'est une déconnexion normale ou anormale (10054).
    /// </summary>
    /// <param name="s"></param>
    /// <param name="messageComplet"></param>
    /// <remarks>Ne bloque pas si appellé sur une connexion fermé</remarks>
    /// <returns>vrai indique que quelque chose à été reçu (succès)</returns>
    public static bool RecevoirTout(this Stream s, out string messageComplet)
    {
      // Pseudo détection pour voir si la connexion est fermée puisque pas accès au TcpClient d'ici
      // Si CanRead est toujours à true, la réception retournera instantanément et ne bloquera pas.
      // 
      if (!s.CanRead) throw new InvalidOperationException("Ne pas appeler sur une connexion déja fermée.");

      string message;
      messageComplet = "";
      bool recuQuelqueChose = false;
      while (s.RecevoirMessage(out message))
      {
        recuQuelqueChose = true;
        messageComplet += message;
      }
      return recuQuelqueChose;
    }

    /// <summary>
    /// L'idée est de recevoir tout sur une connexion TCP
    /// qui n'est pas fermée après par le serveur (keep-alive).
    /// L'idéal serait plutot de recevoir jusqu'à un certain code
    /// comme une fin de ligne, une ligne spéciale vide ou contenant
    /// seulement un point (POP3) ou simplement de recevoir
    /// la quantité prévue. En HTTP le serveur vous donne le content-length
    /// alors vous pouvez vous en servir.
    /// Sinon, si vous pouvez vous servir de cette méthode (moins efficace)
    /// qui regarde dans le flux à savoir s'il y a quelque chose
    /// qui n'a pas été recu (lu).
    /// </summary>
    /// <param name="s"></param>
    /// <param name="messageComplet"></param>
    /// <returns></returns>
    public static bool RecevoirToutKeepAlive(this TcpClient s, out string messageComplet)
    {
      throw new NotImplementedException("TODO");
      //while (s.Connected && s.Available)
    }


    /// <summary>
    /// UDP Cette fonction est un "wrapper" de la méthode Receive().
    /// Va attendre jusqu'à ce qu'un paquet soit reçu.
    /// </summary>
    /// <param name="s">le socket sur lequel il faut recevoir</param>
    /// <param name="remoteEndPoint">stockera le point de connexion de l'émetteur (IP et port)</param>
    /// <param name="message">le contenu du paquet recu encodé en string</param>
    /// <returns>retourne faux si la réception à échouée</returns>
    public static bool RecevoirMessage(this UdpClient s, ref IPEndPoint remoteEndPoint, out string message, Encoding encoding)
    {
      try
      {
        byte[] données = s.Receive(ref remoteEndPoint);
        message = encoding.GetString(données);
        AfficherRéception(message);
        return true;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
      {
        if (Verbose)
          AfficherInfo("Timeout sur le socket.");

        throw; // si vous ne voulez pas catcher l'exception, utilisez RecevoirMessageAvecTimeout et je vous retournerai un beau booléen à la place
      }
      catch (SocketException ex)
      {
        // Probablement un code 10054. Possible surtout si c'est en boucle locale (i.e si l'autre point de connexion est sur la même machine)
        // ou un 10060 (TimeOut) si l'UdpClient a un receiveTimeout de spécifié (ce qui n'est pas le cas par défaut)
        AfficherErreur("Erreur de socket au moment de la réception UDP.\n" +
                                "Code : " + ex.ErrorCode + "(" + ex.SocketErrorCode + ")");
      }
      catch (ObjectDisposedException ex)
      {
        AfficherErreur("Erreur de socket au moment de la réception,\n" +
                                "le socket a été fermé.\n" +
                                ex.Message);
      }
      message = null;
      return false;
    }

    public static bool RecevoirMessage(this UdpClient s, ref IPEndPoint remoteEndPoint, out string message, string encodingString = "ASCII")
    {
      Encoding encoding = Encoding.GetEncoding(encodingString);
      return s.RecevoirMessage(ref remoteEndPoint, out message, encoding);
    }

    public static bool RecevoirMessageAvecTimeout(this UdpClient s, int délaiTimeout, ref IPEndPoint remoteEndPoint, out string message, out bool timeout)
    {
      timeout = false;
      message = string.Empty;

      int vieuxDélaiTimeout = s.Client.ReceiveTimeout; // normalement 0 (blocking)
      s.Client.ReceiveTimeout = délaiTimeout;
      bool recuQuelqueChose = false;
      try
      {
        recuQuelqueChose = s.RecevoirMessage(ref remoteEndPoint, out message);

      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
      {
        // on affiche pas le message d'erreur, c'était prévisible !
        timeout = true;
        recuQuelqueChose = false; // déja par défaut
      }
      catch (Exception ex)
      {
        AfficherErreur(ex.ToString());
      }

      s.Client.ReceiveTimeout = vieuxDélaiTimeout;

      return recuQuelqueChose;
    }


    /// <summary>
    /// Cette méthode permet de fournir un délai et d'attendre
    /// 
    /// On aurait pu aussi utiliser le modèle async-await
    /// ou faire du polling sur UdpClient.Available
    /// </summary>
    /// <param name="s"></param>
    /// <param name="délaiTimeout"></param>
    /// <param name="endPoint"></param>
    /// <param name="message"></param>
    /// <returns>vrai si un message a été recu</returns>
    public static bool RecevoirMessageAvecTimeoutTask(this UdpClient s, int délaiTimeout, ref IPEndPoint endPoint, out string message, out bool timeout)
    {
      timeout = false;
      try
      {

        // option 1 : avec la méthode async et wait sur la tâche
        // La tâche ne s'annule pas donc malgré le timeout, un problème persistait car la tâche
        // récupérait quand même la donnée du buffer
        Task<UdpReceiveResult> t = s.ReceiveAsync();
        t.Wait(délaiTimeout);
        if (!t.IsCompleted)
          throw new TimeoutException("Trop long");

        byte[] données = t.Result.Buffer;
        endPoint = t.Result.RemoteEndPoint;
        message = Encoding.ASCII.GetString(données);

        return true;
      }
      catch (AggregateException aex)
      {
        // Si on ferme le serveur local PENDANT que la tâche est démarrée
        if (aex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionReset)
        {
          AfficherErreur("Erreur de réception UDP");
        }
      }
      catch (SocketException ex)
      {
        // Probablement un code 10054 
        AfficherErreur("Erreur de socket au moment de la réception UDP.\n" +
                                "Code : " + ex.ErrorCode + "(" + ex.SocketErrorCode + ")");
      }
      catch (ObjectDisposedException ex)
      {
        AfficherErreur("Erreur de socket au moment de la réception,\n" +
                                "le socket a été fermé.\n" +
                                ex.Message);
      }
      catch (TimeoutException)
      {
        timeout = true;
        AfficherErreur("Le délai de réception est expiré.");
      }
      message = null;
      return false;
    }

    /// <summary>
    /// Encapsulation ultra-digérée des étapes nécessaires pour faire un serveur concurrent.
    /// 1 création du socket qui deviendra le socket maitre
    /// 2 bind sur toutes les interfaces (c'est normalement ce qu'on veut dans 99% des cas)
    /// 3 listen (activation de la file d'attente)
    /// 4 ajout du socket maitre dans une liste de tous les sockets (évidemment, il y en aura qu'un pour le moment)
    /// </summary>
    /// <param name="portÉcoute"></param>
    /// <param name="socketMaitre">le socket servant de guichet pour recevoir les nouveaux clients (analogie avec attribution d'un numéro au CLSC ou à la SAAQ)</param>
    /// <param name="listeComplèteSockets">la liste contenant le socket maitre</param>
    /// <returns>vrai si tout c'est bien déroulé</returns>
    public static bool PréparerServeurConcurrent(int portÉcoute, out Socket socketMaitre, out List<Socket> listeComplèteSockets)
    {

      try
      {
        socketMaitre = ÉcouterTCP(portÉcoute);
      }
      catch (Exception ex)
      {
        AfficherErreur("Impossible de configurer le socket maitre.");
        AfficherErreur(ex.ToString());
        socketMaitre = null; // l'initialisation est pour satisfaire le compilateur qui ne veut pas retourner sinon
        listeComplèteSockets = null;
        return false;
      }

      listeComplèteSockets = new List<Socket>(); // création du "guichet"
      listeComplèteSockets.Add(socketMaitre);

      return true;

    }


  }


}
