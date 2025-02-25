{
  inputs = { nixpkgs.url = "github:nixos/nixpkgs/nixpkgs-unstable"; };

  outputs = { self, nixpkgs }:
    let pkgs = nixpkgs.legacyPackages.aarch64-darwin;
    in {
      devShell.aarch64-darwin =
        pkgs.mkShell { buildInputs = [ pkgs.nodePackages.prettier pkgs.dotnet-sdk_8 pkgs.omnisharp-roslyn pkgs.rzls ]; };
   };
}
